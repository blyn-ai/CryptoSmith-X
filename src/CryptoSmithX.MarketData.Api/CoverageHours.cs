using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Api;

/// <summary>
/// <c>/v1/coverage/hours</c>: for every enabled segment, hour by hour, how much of the ticker stream
/// was actually kept — and the interruptions the collectors recorded, with their causes.
///
/// <b>What it measures, and only that.</b> The source is <c>market_metric_hour</c>, which the rollup
/// writes from kept snapshots: price, spread, funding, open interest and 25 bps depth. Candles,
/// trades, liquidations and order books have no hourly rollup to read cheaply, and scanning their
/// raw partitions per request was measured on the test host at a minute for thirty days of hourly
/// candles. They are named as not measured in the response rather than left out silently.
///
/// <b>Three states, never two.</b> An hour before a segment's first rolled-up hour is
/// <c>not_collected_yet</c> — we were not watching that venue then. An hour after it with no row is
/// <c>not_observed</c> — we were supposed to be, and no snapshot was kept. An hour with a row is
/// <c>observed</c>, with its counts. Drawing the first two alike would turn "we started on the 13th"
/// into "the venue went dark for a week".
///
/// <b>Why cached.</b> Thirty-one days of the hourly aggregate over every segment ran 2.8 s on the
/// test host. The rows change once an hour, when the rollup closes one, so five minutes of reuse costs a
/// caller nothing true and saves the database the same scan per page view.
/// </summary>
public static partial class CoverageHours
{
    public const int DefaultDays = 7;
    public const int MaxDays = 31;

    /// <summary>Four times what the test host recorded over the widest window (1,249 in 31 days on
    /// 2026-09-14). A window that holds more is said to be cut, never quietly shortened.</summary>
    public const int MaxGaps = 5000;

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<(string? Segment, int Days), (DateTimeOffset At, object Body)> Cache = new();

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex SegmentCode();

    public const string NotCollectedYet = "not_collected_yet";
    public const string NotObserved = "not_observed";
    public const string Observed = "observed";

    /// <summary>The request's own mistakes, before any database work. Null when the request is
    /// usable.</summary>
    public static string? Validate(string? segment, int? days)
    {
        if (days is < 1 or > MaxDays)
        {
            return $"days must be between 1 and {MaxDays}.";
        }

        if (segment is not null && !SegmentCode().IsMatch(segment))
        {
            return "segment must be a segment code, for example kraken-futures.";
        }

        return null;
    }

    public sealed record HourRow(
        string Segment,
        DateTime Hour,
        int Instruments,
        long Snapshots,
        long? Expected,
        int ExpectedKnown,
        int BlindSecondsMax,
        int WithDepth);

    public sealed record HourCell(
        DateTime Hour,
        string State,
        int? Instruments,
        long? Snapshots,
        long? Expected,
        double? Completeness,
        int? BlindSecondsMax,
        int? InstrumentsWithDepth);

    /// <summary>
    /// The dense hour list for one segment, from <paramref name="from"/> through
    /// <paramref name="lastHour"/> inclusive. Pure, so the three states are tested without a
    /// database.
    ///
    /// Completeness is kept snapshots over expected ones, and only when every row of the hour knows
    /// its expected count: rows older than 0017 carry none, and dividing by a partial sum would
    /// overstate the hour. It is not clamped — a value above 1 means the keep interval changed
    /// inside the hour, and hiding that would hide the reason.
    /// </summary>
    public static IReadOnlyList<HourCell> Grid(
        DateTime from, DateTime lastHour, DateTime? firstHour, IReadOnlyDictionary<DateTime, HourRow> rows)
    {
        var cells = new List<HourCell>();
        for (var hour = from; hour <= lastHour; hour = hour.AddHours(1))
        {
            if (rows.TryGetValue(hour, out var r))
            {
                double? completeness = r.ExpectedKnown == r.Instruments && r.Expected is > 0
                    ? Math.Round((double)r.Snapshots / r.Expected.Value, 4)
                    : null;
                cells.Add(new HourCell(hour, Observed, r.Instruments, r.Snapshots,
                    r.ExpectedKnown == r.Instruments ? r.Expected : null,
                    completeness, r.BlindSecondsMax, r.WithDepth));
            }
            else
            {
                var state = firstHour is null || hour < firstHour ? NotCollectedYet : NotObserved;
                cells.Add(new HourCell(hour, state, null, null, null, null, null, null));
            }
        }

        return cells;
    }

    public static async Task<IResult> Get(Db db, string? segment, int? days, CancellationToken ct)
    {
        if (Validate(segment, days) is { } error)
        {
            return Results.BadRequest(new { error });
        }

        var span = days ?? DefaultDays;
        if (Cache.TryGetValue((segment, span), out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return Results.Ok(hit.Body);
        }

        var now = DateTime.UtcNow;
        var from = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddDays(-span);

        await using var conn = await db.OpenAsync(ct);

        var segments = (await conn.QueryAsync<SegmentRow>(new CommandDefinition(
            """
            select s.code as "Code",
                   s.kind as "Kind",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = s.code and i.collect) as "InstrumentsCollected",
                   -- One index probe per instrument on the (instrument, hour) key. The plain
                   -- min over a join scanned the whole table: 5.1 s on the test host, against
                   -- 0.3 s this way, for the same eighteen dates.
                   (select min(f.hour_time) from exchange_instrument i
                      cross join lateral (select h.hour_time from market_metric_hour h
                                           where h.exchange_instrument_id = i.id
                                           order by h.hour_time limit 1) f
                     where i.segment_code = s.code)               as "FirstHour"
              from segment s
             where s.status = 'enabled'
               and (@segment is null or s.code = @segment)
             order by s.code
            """, new { segment }, cancellationToken: ct))).ToList();

        if (segment is not null && segments.Count == 0)
        {
            return Results.NotFound(new { error = $"No enabled segment is called {segment}." });
        }

        var rows = (await conn.QueryAsync<HourRow>(new CommandDefinition(
            """
            select i.segment_code                        as "Segment",
                   h.hour_time                           as "Hour",
                   count(*)::int                         as "Instruments",
                   sum(h.snapshot_count)::bigint         as "Snapshots",
                   sum(h.expected_count)::bigint         as "Expected",
                   count(h.expected_count)::int          as "ExpectedKnown",
                   coalesce(max(h.gap_seconds), 0)::int  as "BlindSecondsMax",
                   count(*) filter (where h.depth_bid_25bps_avg is not null
                                       or h.depth_ask_25bps_avg is not null)::int as "WithDepth"
              from market_metric_hour h
              join exchange_instrument i on i.id = h.exchange_instrument_id
              join segment s on s.code = i.segment_code and s.status = 'enabled'
             where h.hour_time >= @from
               and (@segment is null or i.segment_code = @segment)
             group by i.segment_code, h.hour_time
            """, new { from, segment }, cancellationToken: ct))).ToList();

        var gaps = (await conn.QueryAsync<GapRow>(new CommandDefinition(
            """
            select g.segment_code    as "Segment",
                   g.collector       as "Collector",
                   i.exchange_symbol as "Symbol",
                   g.gap_start       as "Start",
                   g.gap_end         as "End",
                   g.cause           as "Cause",
                   g.detail          as "Detail"
              from collector_gap g
              join segment s on s.code = g.segment_code and s.status = 'enabled'
              left join exchange_instrument i on i.id = g.exchange_instrument_id
             where coalesce(g.gap_end, now()) > @from
               and (@segment is null or g.segment_code = @segment)
             order by g.gap_start desc
             limit @limit
            """, new { from, segment, limit = MaxGaps + 1 }, cancellationToken: ct))).ToList();

        var warnings = new List<string>();
        if (gaps.Count > MaxGaps)
        {
            gaps.RemoveAt(gaps.Count - 1);
            warnings.Add($"More than {MaxGaps} interruptions fall in this window; the oldest were cut. Ask for fewer days or one segment.");
        }

        // The last hour the rollup has closed, for everyone at once: an hour past it is not yet
        // known, and printing it as not_observed would blame the venue for our own pass not having run.
        DateTime? lastHour = rows.Count == 0 ? null : rows.Max(r => r.Hour);
        if (lastHour is null)
        {
            warnings.Add("No hourly rows exist in this window, so no hour can be stated either way.");
        }

        var bySegment = rows.GroupBy(r => r.Segment).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Hour));
        var body = new
        {
            source = "market_metric_hour",
            measures = "Hours in which ticker snapshots (price, spread, funding, open interest, 25 bps depth) were kept, "
                + "per enabled segment, as rolled up from market_snapshot.",
            notMeasured = new[] { "candles", "trades", "liquidations", "order books" },
            states = new
            {
                not_collected_yet = "Before this segment's first rolled-up hour: we were not collecting it yet.",
                not_observed = "After collection began, but no snapshot was kept in this hour.",
                observed = "Snapshots were kept. completeness = snapshots / expected, null when an expected count is unknown.",
            },
            gapsNote = "Interruptions the collectors noticed and recorded. An hour can be incomplete without one: "
                + "a missing interruption is an unknown cause, not proof of continuity.",
            from,
            to = lastHour?.AddHours(1),
            measuredAt = DateTimeOffset.UtcNow,
            segments = segments.Select(s => new
            {
                code = s.Code,
                kind = s.Kind,
                instrumentsCollectedNow = s.InstrumentsCollected,
                firstHour = s.FirstHour,
                hours = lastHour is null
                    ? []
                    : Grid(from, lastHour.Value, s.FirstHour,
                        bySegment.TryGetValue(s.Code, out var own) ? own : new Dictionary<DateTime, HourRow>()),
            }),
            gaps = gaps.Select(g => new
            {
                segment = g.Segment,
                collector = g.Collector,
                symbol = g.Symbol,
                start = g.Start,
                end = g.End,
                cause = g.Cause,
                detail = g.Detail,
            }),
            warnings,
        };

        Cache[(segment, span)] = (DateTimeOffset.UtcNow, body);
        return Results.Ok(body);
    }

    private sealed record SegmentRow(string Code, string Kind, int InstrumentsCollected, DateTime? FirstHour);

    private sealed record GapRow(
        string Segment, string Collector, string? Symbol, DateTime Start, DateTime? End, string Cause, string? Detail);
}

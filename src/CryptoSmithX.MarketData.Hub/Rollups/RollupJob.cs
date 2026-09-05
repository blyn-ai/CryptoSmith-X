using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Rollups;

/// <summary>
/// Builds every derived timeframe from the 1-minute bars. One instance for the whole service: the
/// work is per instrument and the source table is shared.
///
/// The unit of work is a window that has been *touched* — any window holding a 1m bar written or
/// rewritten since the last pass. That is what lets a bar arriving late repair its parents instead
/// of leaving them wrong forever, and it keeps a pass proportional to what actually changed. The
/// aggregation itself runs as one <c>insert ... select ... group by ... on conflict</c> statement
/// per timeframe — no 1-minute bar ever crosses the wire into this process. At 1,500 instruments a
/// touched daily window alone is ~2.1M minute rows; pulling that into memory to <c>GroupBy</c> in
/// C# is what took the pass to 142s and knocked snapshots offline. <see cref="Rollup"/> is kept as
/// the arithmetic's specification — unit-tested in isolation — even though this class no longer
/// calls it; the SQL below is required to mean the same thing.
/// </summary>
public sealed class RollupJob
{
    /// <summary>
    /// How far back before the watermark to re-read. Covers rows committed while the previous pass
    /// was already running: the watermark is the point the pass processed up to, not the instant it
    /// finished writing.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The most <c>updated_at</c> a single pass will chew through. Without a bound, a job that has
    /// fallen behind has to swallow the whole arrears in one statement, hits the command timeout,
    /// fails, and therefore never records progress — so the next pass faces the same arrears plus a
    /// minute. That is not a slow recovery, it is no recovery, and it is what four hours of prod
    /// looked like. Bounded, a pass takes a bite it can finish, writes the watermark, and the next
    /// one starts where it stopped.
    /// </summary>
    private static readonly TimeSpan MaxStep = TimeSpan.FromMinutes(30);

    /// <summary>First-ever pass (no persisted watermark yet): bounded to a day rather than the
    /// start of the table, so a fresh deploy never re-touches the whole history. Deeper history,
    /// when it is actually wanted, is a deliberate backfill — not something a cold start does
    /// silently.</summary>
    private static readonly TimeSpan ColdStartWindow = TimeSpan.FromDays(1);

    /// <summary>A catch-up bite is bigger than a routine one; 30 s (Npgsql's default) is not the
    /// budget for it. The bound above is what keeps a pass short — this only stops the timeout from
    /// being the thing that decides.</summary>
    private const int AggregateTimeoutSeconds = 180;

    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RollupJob> _logger;
    private readonly string _serviceExchange;
    private readonly string _collector;

    /// <summary>
    /// <paramref name="serviceExchange"/> and <paramref name="collector"/> identify this job's own
    /// row in <c>collector_status</c> — the same row <see cref="Ingestion.CollectorLoop"/> already
    /// upserts <c>last_success_at</c> into after every run that did not throw. Reading it back here,
    /// instead of keeping a private field, is the watermark: it survives a restart for free, and it
    /// only ever advances on success (a failed or cancelled pass leaves <c>last_success_at</c>
    /// untouched, so the next pass retries the same window instead of either losing it or rewinding
    /// to the epoch).
    /// </summary>
    public RollupJob(
        DbSettings settings, Db db, TimeProvider clock, ILogger<RollupJob> logger,
        string serviceExchange, string collector)
    {
        _settings = settings;
        _db = db;
        _clock = clock;
        _logger = logger;
        _serviceExchange = serviceExchange;
        _collector = collector;
    }

    /// <summary>The slice of <c>updated_at</c> this pass owns. <paramref name="watermark"/> is how
    /// far the source has been processed, so the bite is measured from it, not from the slacked
    /// start — otherwise every pass would give back ten of the thirty minutes it just gained and
    /// arrears would drain at a third of the intended rate. Pure, so both bounds are asserted
    /// directly.</summary>
    internal static (DateTimeOffset Since, DateTimeOffset Until) Window(
        DateTimeOffset? watermark, DateTimeOffset startedAt)
    {
        var since = watermark is { } w ? w - Slack : startedAt - ColdStartWindow;
        var step = (watermark ?? since) + MaxStep;
        return (since, step < startedAt ? step : startedAt);
    }

    /// <summary>
    /// Which timeframe <paramref name="tf"/> is built from: the largest configured one below it
    /// that divides it evenly, else the 1-minute base. A day built from 24 hourly bars reads 24
    /// rows per instrument; built from minutes it reads 1,440, which at 1,482 instruments is the
    /// 2.1M-row window that made 720 and 1440 unaffordable and got them switched off. The rest of
    /// the pass makes this safe: timeframes are processed in ascending order, so a source is always
    /// already rebuilt when its consumer runs, and every aggregate here is associative — max of
    /// maxima, sum of sums, first and last by time — so a bar of bars equals a bar of minutes.
    /// </summary>
    internal static int SourceFor(int tf, IReadOnlyCollection<int> configured) =>
        configured.Where(s => s < tf && tf % s == 0).DefaultIfEmpty(1).Max();

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        var configured = (await _settings.CurrentAsync(ct))
            .CollectionSettingIntList("rollup", "derived_timeframes")
            .Where(t => t > 1).Distinct().OrderBy(t => t).ToList();

        await using var conn = await _db.OpenAsync(ct);
        await Partitions.EnsureAsync(conn, startedAt, ct);

        // Npgsql hands a scalar timestamptz back as DateTime, not DateTimeOffset — same as every
        // row-mapped timestamp in this file (see Utc below); asking Dapper for DateTimeOffset
        // directly throws on the Convert.ChangeType it falls back to for a scalar read.
        //
        // watermark_at is the job's own, and falls back to last_success_at the first time it runs
        // after the column was added so that upgrade does not read as a cold start. It advances
        // only at the end of a pass that wrote everything: a pass that throws leaves it alone and
        // the next one retakes the same slice.
        var mark = await conn.QuerySingleOrDefaultAsync<MarkRow>(
            new CommandDefinition(
                """
                select coalesce(watermark_at, last_success_at) as Watermark, now() as DbNow
                  from collector_status
                 where exchange_code = @exchange and collector = @collector
                """,
                new { exchange = _serviceExchange, collector = _collector },
                cancellationToken: ct));

        var (since, until) = Window(mark?.Watermark is { } w ? Utc(w) : null, startedAt);

        // Rows this pass writes carry the database's now(), not ours, so what counts as "rebuilt
        // just now" has to be read from the same clock. Each higher timeframe consumes exactly what
        // the level below it rewrote in this pass — precise, and it does not grow with arrears the
        // way `updated_at >= since` would while catching up.
        var passStart = mark?.DbNow is { } n ? Utc(n) : startedAt;

        var written = 0;
        foreach (var tf in configured)
        {
            var source = SourceFor(tf, configured);
            var seconds = tf * 60L;

            // Every touched window, aggregated where it lives. "touched" only widens the CTE's own
            // distinct scan; the join and group by then aggregate the window's full set of source
            // bars without any of them ever leaving Postgres. At the base the slice is bounded on
            // both sides — that bite is the unit of progress. Above the base there is no upper
            // bound to apply: those rows were written moments ago by this very pass.
            // open/close are the array's first/last element by time, not arrival order — the same
            // rule Rollup.Build encodes, expressed as `order by open_time`. bar_count sums the
            // source's own counts rather than counting rows, so it keeps meaning minutes covered at
            // every level of the cascade.
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                $"""
                with touched as (
                    select distinct
                           c.exchange_instrument_id,
                           to_timestamp(floor(extract(epoch from c.open_time) / @seconds) * @seconds) as window_start
                      from market_candle c
                     where c.timeframe = @source
                       {(source == 1 ? "and c.updated_at >= @since and c.updated_at < @until" : "and c.updated_at >= @passStart")}
                ),
                windows as (
                    select t.exchange_instrument_id,
                           t.window_start,
                           sum(c.bar_count)::smallint                            as bar_count,
                           (array_agg(c.open  order by c.open_time asc))[1]      as open,
                           max(c.high)                                           as high,
                           min(c.low)                                            as low,
                           (array_agg(c.close order by c.open_time desc))[1]     as close,
                           sum(c.volume)                                         as volume,
                           case when bool_and(c.trade_count is not null)
                                then sum(c.trade_count) else null end            as trade_count
                      from touched t
                      join market_candle c
                        on  c.exchange_instrument_id = t.exchange_instrument_id
                        and c.timeframe = @source
                        and c.open_time >= t.window_start
                        and c.open_time <  t.window_start + make_interval(secs => @seconds)
                     where t.window_start + make_interval(secs => @seconds) <= now()
                     group by t.exchange_instrument_id, t.window_start
                )
                insert into market_candle (
                    exchange_instrument_id, timeframe, open_time,
                    open, high, low, close, volume, trade_count, bar_count, updated_at)
                select exchange_instrument_id, @tf, window_start,
                       open, high, low, close, volume, trade_count, bar_count, now()
                  from windows
                on conflict (exchange_instrument_id, timeframe, open_time) do update set
                    open        = excluded.open,
                    high        = excluded.high,
                    low         = excluded.low,
                    close       = excluded.close,
                    volume      = excluded.volume,
                    trade_count = excluded.trade_count,
                    bar_count   = excluded.bar_count,
                    updated_at  = excluded.updated_at
                """,
                new
                {
                    seconds = (double)seconds,
                    since,
                    until,
                    passStart,
                    tf = (short)tf,
                    source = (short)source,
                },
                commandTimeout: AggregateTimeoutSeconds,
                cancellationToken: ct));
            written += affected;
        }

        // Last step: the hourly microstructure slice. Open interest, spread and depth die with the
        // 90-day snapshot rotation, so every closed hour touched in this slice is folded into
        // market_metric_hour, which is not rotated. Recomputed whole on every touch, the same way a
        // derived candle is — a late snapshot repairs its hour instead of leaving it wrong.
        //
        // Whole is the operative word, and it was not: the read started at `since`, so an hour was
        // rebuilt from the handful of snapshots that happened to fall inside the window and the
        // upsert then overwrote the complete row with that fragment. Prod hours held snapshot_count
        // of 1 to 4 where ~360 belonged — an "hourly average spread" averaged over one sample, in
        // the one table that exists because this data cannot be re-fetched later. Reading from the
        // top of `since`'s hour costs at most one extra hour of snapshots and makes every hour the
        // pass touches a complete one, since an hour containing a row at or after `since` cannot
        // start earlier than `since`'s own hour.
        var metricRows = await conn.QueryAsync<MetricRow>(new CommandDefinition(
            """
            select s.exchange_instrument_id          as InstrumentId,
                   date_trunc('hour', s.received_at) as HourTime,
                   s.received_at                     as ReceivedAt,
                   s.bid_price                        as BidPrice,
                   s.ask_price                        as AskPrice,
                   s.open_interest                    as OpenInterest,
                   s.funding_rate                     as FundingRate,
                   s.depth_bid_25bps                  as DepthBid25,
                   s.depth_ask_25bps                  as DepthAsk25
              from market_snapshot s
             where s.received_at >= date_trunc('hour', @since)
               and s.received_at <  least(date_trunc('hour', @until) + interval '1 hour',
                                          date_trunc('hour', now()))
            """,
            new { since, until },
            commandTimeout: AggregateTimeoutSeconds,
            cancellationToken: ct));

        var metrics = 0;
        foreach (var hour in metricRows.GroupBy(r => (r.InstrumentId, r.HourTime)))
        {
            var bar = MetricHour.Aggregate(
                Utc(hour.Key.HourTime),
                hour.Select(r => new MetricSnapshot(
                    Utc(r.ReceivedAt), r.BidPrice, r.AskPrice, r.OpenInterest, r.FundingRate,
                    r.DepthBid25, r.DepthAsk25)).ToList());

            await conn.ExecuteAsync(new CommandDefinition(
                """
                -- expected_count and gap_seconds are what make snapshot_count readable. Thirty
                -- observations in an hour is either a market that went quiet or an hour we did
                -- not watch, and those two demand opposite conclusions. expected_count comes from
                -- the configured cadence; gap_seconds from how much of the hour collection_gap
                -- says we were blind for. An hour with gap_seconds = 0 and a low count is the
                -- venue's silence and is data; an hour with gap_seconds > 0 is our absence and
                -- is not.
                insert into market_metric_hour (
                    exchange_instrument_id, hour_time, open_interest_last, funding_rate_last,
                    spread_bps_avg, depth_bid_25bps_avg, depth_ask_25bps_avg, snapshot_count,
                    expected_count, gap_seconds, updated_at)
                select @InstrumentId, @HourTime, @OpenInterestLast, @FundingRateLast,
                       @SpreadBpsAvg, @DepthBid25BpsAvg, @DepthAsk25BpsAvg, @SnapshotCount,
                       -- Expected HISTORY rows, which is not the same as expected passes and was
                       -- the first version's mistake: snapshot_count counts rows in
                       -- market_snapshot, and SnapshotCollector writes one per instrument per
                       -- minute however often it polls. Against 3600/interval an healthy hour
                       -- read as 58 of 360. The floor of 60 s is that flush; a poll slower than a
                       -- minute becomes the binding constraint instead, hence the greatest().
                       least(3600 / greatest(coalesce(ec.interval_s, c.default_interval_s, 10), 60),
                             32767)::smallint,
                       coalesce(gap.seconds, 0),
                       now()
                  from exchange_instrument ei
                  left join exchange_collection ec
                         on ec.exchange_code = ei.exchange_code and ec.collection_code = 'snapshot'
                  left join collection c on c.code = 'snapshot'
                  left join lateral (
                      select sum(extract(epoch from
                                 least(coalesce(g.gap_end, @HourTime + interval '1 hour'),
                                       @HourTime + interval '1 hour')
                               - greatest(g.gap_start, @HourTime)))::int as seconds
                        from collection_gap g
                       where g.exchange_code = ei.exchange_code
                         -- Every collector that feeds this row, not just snapshot. depth and funding
                         -- land in the same hourly record, so an hour blind to depth was being
                         -- reported as fully observed. An instrument-scoped gap counts only for that
                         -- instrument; an exchange-wide one counts for all of them.
                         and g.collector in ('snapshot', 'depth', 'funding')
                         and (g.exchange_instrument_id is null
                              or g.exchange_instrument_id = ei.id)
                         and g.gap_start < @HourTime + interval '1 hour'
                         and coalesce(g.gap_end, now()) > @HourTime) gap on true
                 where ei.id = @InstrumentId
                on conflict (exchange_instrument_id, hour_time) do update set
                    open_interest_last  = excluded.open_interest_last,
                    funding_rate_last   = excluded.funding_rate_last,
                    spread_bps_avg      = excluded.spread_bps_avg,
                    depth_bid_25bps_avg = excluded.depth_bid_25bps_avg,
                    depth_ask_25bps_avg = excluded.depth_ask_25bps_avg,
                    snapshot_count      = excluded.snapshot_count,
                    expected_count      = excluded.expected_count,
                    gap_seconds         = excluded.gap_seconds,
                    updated_at          = excluded.updated_at
                """,
                new
                {
                    InstrumentId = hour.Key.InstrumentId,
                    bar.HourTime,
                    bar.OpenInterestLast,
                    bar.FundingRateLast,
                    bar.SpreadBpsAvg,
                    bar.DepthBid25BpsAvg,
                    bar.DepthAsk25BpsAvg,
                    bar.SnapshotCount,
                },
                cancellationToken: ct));
            metrics++;
        }

        // Only now: everything in this slice is on disk, so the slice is done. A pass that threw
        // anywhere above never reaches this line, which is what makes a failure retry the same
        // slice instead of skipping it.
        await conn.ExecuteAsync(new CommandDefinition(
            "update collector_status set watermark_at = @until where exchange_code = @exchange and collector = @collector",
            new { until, exchange = _serviceExchange, collector = _collector },
            cancellationToken: ct));

        if (written > 0 || metrics > 0)
        {
            _logger.LogDebug(
                "Rollup wrote {Rows} derived bars and {Metrics} metric hours up to {Until:o} ({Lag:0.#} min behind)",
                written, metrics, until, (startedAt - until).TotalMinutes);
        }

        return written;
    }

    // Npgsql hands back timestamptz as DateTime with Kind=Utc; converted at the boundary below.
    private static DateTimeOffset Utc(DateTime t) =>
        new(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>Named, not a ValueTuple: Dapper binds tuple elements by position, and this file has
    /// been bitten by column order before.</summary>
    private sealed record MarkRow(DateTime? Watermark, DateTime? DbNow);

    private sealed record MetricRow(
        int InstrumentId,
        DateTime HourTime,
        DateTime ReceivedAt,
        double BidPrice,
        double AskPrice,
        double OpenInterest,
        double FundingRate,
        double? DepthBid25,
        double? DepthAsk25);
}

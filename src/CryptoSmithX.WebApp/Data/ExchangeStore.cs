using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// The integrations registry. A row here is a segment — one venue's trading surface, with its own
/// base URL, symbol space and adapter — joined to the venue that owns it. Lifecycle columns
/// (status, name, description) are the admin's to write; everything observed — collector state,
/// durations, instrument counts — is read-only here and always derived, never stored on the row.
/// </summary>
public static class ExchangeStore
{
    public static async Task<IReadOnlyList<ExchangeListItem>> ListAsync(DbConnection conn, CancellationToken ct)
    {
        return (await conn.QueryAsync<ExchangeListItem>(new CommandDefinition(
            """
            select e.code                as "Code",
                   e.name                as "Name",
                   e.status              as "Status",
                   e.description         as "Description",
                   e.exchange_code       as "ExchangeCode",
                   x.name                as "ExchangeName",
                   e.kind                as "Kind",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = e.code and i.status = 'trading')          as "TradingInstruments",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = e.code)                                   as "KnownInstruments",
                   (select max(s.consecutive_failures) from collector_status s
                     where s.segment_code = e.code)                                   as "MaxFailures",
                   (select avg(s.avg_duration_ms) from collector_status s
                     where s.segment_code = e.code and s.avg_duration_ms is not null) as "AvgDurationMs",
                   (select extract(epoch from now() - max(s.last_success_at))::double precision
                      from collector_status s
                     where s.segment_code = e.code and s.collector = 'discovery')     as "DiscoveryAgeSeconds"
              from segment e
              join exchange x on x.code = e.exchange_code
             order by case e.status when 'enabled' then 0 when 'maintenance' then 1
                                    when 'disabled' then 2 when 'planned' then 3 else 4 end,
                      e.code
            """,
            cancellationToken: ct))).ToList();
    }

    public static async Task<ExchangeDetails?> GetAsync(DbConnection conn, string code, CancellationToken ct)
    {
        var exchange = await conn.QuerySingleOrDefaultAsync<ExchangeListItem>(new CommandDefinition(
            """
            select e.code          as "Code",
                   e.name          as "Name",
                   e.status        as "Status",
                   e.description   as "Description",
                   e.exchange_code as "ExchangeCode",
                   x.name          as "ExchangeName",
                   e.kind          as "Kind",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = e.code and i.status = 'trading') as "TradingInstruments",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = e.code)                          as "KnownInstruments",
                   null::int                                                  as "MaxFailures",
                   null::double precision                                     as "AvgDurationMs",
                   null::double precision                                     as "DiscoveryAgeSeconds"
              from segment e
              join exchange x on x.code = e.exchange_code
             where e.code = @code
            """,
            new { code },
            cancellationToken: ct));

        if (exchange is null)
        {
            return null;
        }

        var collectors = (await conn.QueryAsync<ExchangeCollectorRow>(new CommandDefinition(
            """
            select s.collector                                                          as "Collector",
                   extract(epoch from now() - s.last_success_at)::double precision      as "LastSuccessAgeSeconds",
                   s.consecutive_failures                                               as "ConsecutiveFailures",
                   s.instruments_expected                                               as "InstrumentsExpected",
                   s.last_duration_ms                                                   as "LastDurationMs",
                   s.avg_duration_ms                                                    as "AvgDurationMs",
                   s.last_error                                                         as "LastError",
                   extract(epoch from now() - s.last_error_at)::double precision        as "LastErrorAgeSeconds"
              from collector_status s
             where s.segment_code = @code
             order by s.collector
            """,
            new { code },
            cancellationToken: ct))).ToList();

        // Stalest trading instruments — the oldest snapshots, which is where a failing feed shows.
        var stalest = (await conn.QueryAsync<StaleInstrument>(new CommandDefinition(
            """
            select i.id as "Id",
                   i.exchange_symbol as "Symbol",
                   extract(epoch from now() - l.received_at)::double precision as "AgeSeconds"
              from exchange_instrument i
              join market_snapshot_latest l on l.exchange_instrument_id = i.id
             where i.segment_code = @code and i.status = 'trading'
             order by l.received_at asc limit 6
            """,
            new { code },
            cancellationToken: ct))).ToList();

        // Snapshot throughput per 5 min over 2 h, read from the run log — see the note on
        // DashboardStore.SparkSql: counting market_snapshot rows re-scanned the archive on
        // every render and got slower as the archive grew.
        var throughput = (await conn.QueryAsync<double>(new CommandDefinition(
            """
            with buckets as (select generate_series(date_trunc('hour', now()) - interval '2 hours', now(), interval '5 minutes') as b),
            agg as (
                select to_timestamp(floor(extract(epoch from r.started_at) / 300) * 300) as b,
                       sum(r.items)::double precision as rows
                  from collector_run r
                 where r.segment_code = @code and r.collector = 'snapshot' and r.ok
                   and r.started_at >= date_trunc('hour', now()) - interval '2 hours'
                 group by 1
            )
            select coalesce(agg.rows, 0)
              from buckets left join agg on agg.b = buckets.b
             order by buckets.b
            """,
            new { code },
            cancellationToken: ct))).ToList();

        var head = await conn.QuerySingleAsync<(string Adapter, string? BaseUrl, string? ChartsUrl, string? WsUrl, string[] QuoteAssets, string[] Blacklist, string? UpdatedBy)>(
            new CommandDefinition(
                """
                select adapter      as "Adapter",
                       base_url     as "BaseUrl",
                       charts_url   as "ChartsUrl",
                       ws_url       as "WsUrl",
                       quote_assets as "QuoteAssets",
                       blacklist    as "Blacklist",
                       updated_by   as "UpdatedBy"
                  from segment
                 where code = @code
                """,
                new { code },
                cancellationToken: ct));

        // The five interval overrides used to be columns on exchange; since 0014 they are cells of
        // the segment_dataset matrix (in seconds uniformly). This view keeps the page's existing
        // shape (discovery/funding still in minutes) without touching Details.cshtml — that
        // conversion is the only thing this query still owns on their behalf.
        var overrides = (await conn.QueryAsync<(string Dataset, int? IntervalS)>(new CommandDefinition(
            """
            select dataset_code as "Dataset", interval_s as "IntervalS"
              from segment_dataset
             where segment_code = @code
               and dataset_code in ('discovery', 'snapshot', 'depth', 'candles', 'funding')
            """,
            new { code },
            cancellationToken: ct)))
            .ToDictionary(r => r.Dataset, r => r.IntervalS, StringComparer.Ordinal);

        var config = new ExchangeConfigRow
        {
            Adapter = head.Adapter,
            BaseUrl = head.BaseUrl,
            ChartsUrl = head.ChartsUrl,
            WsUrl = head.WsUrl,
            QuoteAssets = head.QuoteAssets,
            Blacklist = head.Blacklist,
            SnapshotIntervalS = overrides.GetValueOrDefault("snapshot"),
            CandleIntervalS = overrides.GetValueOrDefault("candles"),
            DiscoveryIntervalMin = overrides.GetValueOrDefault("discovery") is { } d ? d / 60 : null,
            FundingIntervalMin = overrides.GetValueOrDefault("funding") is { } f ? f / 60 : null,
            DepthIntervalS = overrides.GetValueOrDefault("depth"),
            UpdatedBy = head.UpdatedBy,
        };

        // Dataset defaults, to show as placeholders where an override is empty — the successor to
        // the old global setting values, which moved into dataset.default_interval_s in 0014.
        var defaults = (await conn.QueryAsync<(string Code, int DefaultIntervalS)>(new CommandDefinition(
            """
            select code as "Code", default_interval_s as "DefaultIntervalS"
              from dataset
             where code in ('discovery', 'snapshot', 'depth', 'candles', 'funding')
            """,
            cancellationToken: ct))).ToList();

        var globals = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["snapshot_interval_s"] = defaults.Single(r => r.Code == "snapshot").DefaultIntervalS,
            ["candle_interval_s"] = defaults.Single(r => r.Code == "candles").DefaultIntervalS,
            ["discovery_interval_min"] = defaults.Single(r => r.Code == "discovery").DefaultIntervalS / 60,
            ["funding_interval_min"] = defaults.Single(r => r.Code == "funding").DefaultIntervalS / 60,
            ["depth_interval_s"] = defaults.Single(r => r.Code == "depth").DefaultIntervalS,
        };

        // Intervals this venue was not observed for. Open ones first: a hole that has not closed
        // is still happening, and it is the only thing on this page that is about right now.
        var gaps = (await conn.QueryAsync<CollectorGapRow>(new CommandDefinition(
            """
            select g.collector                                            as "Collector",
                   g.gap_start                                            as "GapStart",
                   g.gap_end                                              as "GapEnd",
                   g.cause                                                as "Cause",
                   g.detail                                               as "Detail",
                   extract(epoch from coalesce(g.gap_end, now()) - g.gap_start)::double precision
                                                                          as "SecondsLong"
              from collector_gap g
             where g.segment_code = @code
               and (g.gap_end is null or g.gap_start > now() - interval '48 hours')
             order by (g.gap_end is null) desc, g.gap_start desc
             limit 20
            """,
            new { code },
            cancellationToken: ct))).ToList();

        return new ExchangeDetails(
            exchange, config, globals, collectors, stalest, throughput, await RunStore.LatencyAsync(conn, code, ct),
            await FeedStore.ListAsync(conn, code, ct), await FeedStore.DialogsAsync(conn, code, ct), gaps);
    }

    private static readonly string[] AllowedStatuses =
        ["planned", "enabled", "disabled", "maintenance", "abandoned"];

    /// <summary>
    /// Writes the editable configuration of an exchange, stamping who did it. Status is NOT written
    /// here — it is the guarded control on the overview (see <see cref="SetStatusAsync"/>) — so this
    /// form can never take a venue offline by accident. Adapter is bound to the code and read-only.
    /// Interval values are per-segment overrides, null means "use the dataset default"; since
    /// 0014 they live in segment_dataset, not on exchange, so this writes both places in one
    /// transaction — the exchange row keeps its old shape externally, but it has nowhere left to
    /// write those five values into.
    /// </summary>
    public static async Task<bool> SaveAsync(DbConnection conn, ExchangeSaveInput input, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            update segment
               set name        = @Name,
                   description = nullif(@Description, ''),
                   base_url    = nullif(@BaseUrl, ''),
                   charts_url  = nullif(@ChartsUrl, ''),
                   ws_url      = nullif(@WsUrl, ''),
                   quote_assets = @QuoteAssets,
                   blacklist   = @Blacklist,
                   updated_by  = @UpdatedBy,
                   updated_at  = now()
             where code = @Code
            """,
            input, tx, cancellationToken: ct));

        if (rows != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // The matrix is always complete (0014's invariant), so every one of these UPDATEs matches
        // exactly the row backfilled by the migration — no insert branch needed.
        await UpdateIntervalAsync(conn, tx, input.Code, "snapshot", input.SnapshotIntervalS, input.UpdatedBy, ct);
        await UpdateIntervalAsync(conn, tx, input.Code, "candles", input.CandleIntervalS, input.UpdatedBy, ct);
        await UpdateIntervalAsync(conn, tx, input.Code, "depth", input.DepthIntervalS, input.UpdatedBy, ct);
        await UpdateIntervalAsync(conn, tx, input.Code, "discovery", input.DiscoveryIntervalMin is { } d ? d * 60 : null, input.UpdatedBy, ct);
        await UpdateIntervalAsync(conn, tx, input.Code, "funding", input.FundingIntervalMin is { } f ? f * 60 : null, input.UpdatedBy, ct);

        await tx.CommitAsync(ct);
        return true;
    }

    private static Task UpdateIntervalAsync(
        DbConnection conn, DbTransaction tx, string segmentCode, string datasetCode, int? intervalS, string? updatedBy, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            update segment_dataset
               set interval_s = @intervalS, updated_by = @updatedBy, updated_at = now()
             where segment_code = @segmentCode and dataset_code = @datasetCode
            """,
            new { segmentCode, datasetCode, intervalS, updatedBy }, tx, cancellationToken: ct));

    /// <summary>
    /// The guarded status change — the only control that stops (or starts) dataset. Returns false
    /// on an unknown exchange or a status the CHECK would reject. The confirm-code guard lives in the
    /// controller; this stamps who and when.
    /// </summary>
    public static async Task<bool> SetStatusAsync(
        DbConnection conn, string code, string status, string? updatedBy, CancellationToken ct)
    {
        if (!AllowedStatuses.Contains(status))
        {
            return false;
        }

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "update segment set status = @status, updated_by = @updatedBy, updated_at = now() where code = @code",
            new { code, status, updatedBy },
            cancellationToken: ct));
        return rows == 1;
    }

    /// <summary>
    /// Connection health for the list — observed, computed, never stored. Deliberately interval-blind:
    /// consecutive_failures is maintained by the collector loop itself, so it needs no knowledge of
    /// per-collector cadences here.
    /// </summary>
    public static string Health(ExchangeListItem e) => e.Status switch
    {
        "enabled" when e.MaxFailures is >= 3 => "error",
        "enabled" when e.MaxFailures is >= 1 => "warning",
        "enabled" when e.KnownInstruments == 0 => "warning",
        "enabled" => "ok",
        _ => "none",
    };
}

/// <summary>Run history (0009): the list behind "runs →" and the window-based "what arrived" view.</summary>
public static class RunStore
{
    public static async Task<IReadOnlyList<CollectorRunRow>> ListAsync(
        DbConnection conn, string code, string? collector, CancellationToken ct)
    {
        return (await conn.QueryAsync<CollectorRunRow>(new CommandDefinition(
            """
            select id as "Id", collector as "Collector", started_at as "StartedAt",
                   duration_ms as "DurationMs", ok as "Ok", error as "Error", items as "Items"
              from collector_run
             where segment_code = @code and (@collector is null or collector = @collector)
             order by started_at desc
             limit 200
            """,
            new { code, collector },
            cancellationToken: ct))).ToList();
    }

    /// <summary>Per-collector average duration in 15-min buckets over 12 h, for the trend panel.</summary>
    public static async Task<IReadOnlyList<LatencySeries>> LatencyAsync(
        DbConnection conn, string code, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<(string Collector, int Bucket, double AvgMs)>(new CommandDefinition(
            """
            select collector,
                   (floor(extract(epoch from now() - started_at) / 900))::int as bucket,
                   avg(duration_ms)::double precision
              from collector_run
             where segment_code = @code and started_at > now() - interval '12 hours' and ok
             group by collector, bucket
            """,
            new { code },
            cancellationToken: ct))).ToList();

        return rows.GroupBy(r => r.Collector).OrderBy(g => g.Key).Select(g =>
        {
            var byBucket = g.ToDictionary(r => r.Bucket, r => r.AvgMs);
            // bucket 47 = oldest, 0 = now; absent bucket repeats the last known value so the
            // line stays continuous without inventing a zero.
            var series = new List<double>(48);
            var last = 0.0;
            for (var b = 47; b >= 0; b--)
            {
                if (byBucket.TryGetValue(b, out var v)) { last = v; }
                series.Add(last);
            }
            return new LatencySeries(g.Key, series);
        }).ToList();
    }

    public static async Task<RunDetails?> GetAsync(DbConnection conn, long id, CancellationToken ct)
    {
        var run = await conn.QuerySingleOrDefaultAsync<(string SegmentCode, long Id, string Collector, DateTime StartedAt, int DurationMs, bool Ok, string? Error, int? Items)?>(
            new CommandDefinition(
                """
                select segment_code as "SegmentCode", id as "Id", collector as "Collector",
                       started_at as "StartedAt", duration_ms as "DurationMs", ok as "Ok",
                       error as "Error", items as "Items"
                  from collector_run where id = @id
                """,
                new { id },
                cancellationToken: ct));
        if (run is null)
        {
            return null;
        }

        var r = run.Value;
        // The window: rows stamped between the run's start and its end (+2 s of write slack).
        // Data is upserted with no run id, so time is the only honest join.
        // The attribution window runs to the NEXT run of the same collector: work like the
        // minute-history flush lands in exactly one run instead of falling between two.
        // For the newest run the fallback is the run's own span plus write slack.
        var winStart = r.StartedAt;
        var nextStart = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            select min(started_at) from collector_run
             where segment_code = @code and collector = @collector and started_at > @start
            """,
            new { code = r.SegmentCode, collector = r.Collector, start = r.StartedAt },
            cancellationToken: ct));
        var winEnd = nextStart ?? r.StartedAt.AddMilliseconds(r.DurationMs).AddSeconds(2);

        (string caption, string sql) = r.Collector switch
        {
            // The loop upserts market_snapshot_latest every run but writes minute-history only
            // once a minute — history alone left most run pages empty. The union adds latest rows
            // still stamped inside this window (i.e. not yet overwritten by a newer run).
            "snapshot" => ("snapshot rows stamped inside the run window (minute-history plus current-latest)",
                """
                select "Symbol", "What", "When" from (
                    select i.exchange_symbol as "Symbol", 'last ' || round(m.last_price::numeric, 4) as "What", m.received_at as "When"
                      from market_snapshot m join exchange_instrument i on i.id = m.exchange_instrument_id
                     where i.segment_code = @code and m.received_at >= @winStart and m.received_at < @winEnd
                    union all
                    select i.exchange_symbol, 'last ' || round(l.last_price::numeric, 4), l.received_at
                      from market_snapshot_latest l join exchange_instrument i on i.id = l.exchange_instrument_id
                     where i.segment_code = @code and l.received_at >= @winStart and l.received_at < @winEnd
                ) w order by "When" desc, "Symbol"
                """),
            "depth" => ("latest-snapshot depth measurements stamped inside the window",
                """
                select i.exchange_symbol as "Symbol", 'depth25 ' || coalesce(round(l.depth_bid_25bps::numeric, 0)::text, '—') as "What", l.depth_at as "When"
                  from market_snapshot_latest l join exchange_instrument i on i.id = l.exchange_instrument_id
                 where i.segment_code = @code and l.depth_at >= @winStart and l.depth_at < @winEnd
                 order by l.depth_at desc
                """),
            "discovery" => ("instruments confirmed by this discovery pass",
                """
                select i.exchange_symbol as "Symbol", i.status as "What", i.last_seen_at as "When"
                  from exchange_instrument i
                 where i.segment_code = @code and i.last_seen_at >= @winStart and i.last_seen_at < @winEnd
                 order by i.exchange_symbol
                """),
            "candles" => ("1m bars written or repaired inside the window",
                """
                select i.exchange_symbol as "Symbol", '1m ' || to_char(c.open_time, 'HH24:MI') || ' close ' || round(c.close::numeric, 4) as "What", c.updated_at as "When"
                  from market_candle c join exchange_instrument i on i.id = c.exchange_instrument_id
                 where i.segment_code = @code and c.timeframe = 1 and c.updated_at >= @winStart and c.updated_at < @winEnd
                 order by c.updated_at desc
                """),
            "rollup" => ("derived bars recomputed inside the window",
                """
                select i.exchange_symbol as "Symbol", c.timeframe || 'm ' || to_char(c.open_time, 'HH24:MI') as "What", c.updated_at as "When"
                  from market_candle c join exchange_instrument i on i.id = c.exchange_instrument_id
                 where i.segment_code = @code and c.timeframe > 1 and c.updated_at >= @winStart and c.updated_at < @winEnd
                 order by c.updated_at desc
                """),
            _ => ("funding_rate_history has no insert stamp (funding_time is the payment time) — the run's items counter is the source of truth here",
                "select null::text as \"Symbol\", null::text as \"What\", null::timestamptz as \"When\" where false"),
        };

        // Count and page in SQL: a backfill run's window matches tens of thousands of
        // candle rows, and dragging them into memory to call .Count froze the page.
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ({sql}) w",
            new { code = r.SegmentCode, winStart, winEnd }, cancellationToken: ct));
        var rows = (await conn.QueryAsync<RunDataRow>(new CommandDefinition(
            sql + " limit 40",
            new { code = r.SegmentCode, winStart, winEnd }, cancellationToken: ct))).ToList();

        // Each collector explains its own empty page instead of one generic wall of maybes.
        var emptyNote = r.Collector switch
        {
            "snapshot" => $"This run refreshed the live snapshot for its {r.Items?.ToString() ?? "—"} instruments; that state has since been overwritten by newer runs. Minute-history rows are flushed once a minute, and none fell to this run — open a run that crossed a minute boundary to see them.",
            "depth" => "Depth stamps live on the latest-state rows and are overwritten by newer sweeps — only the most recent run still shows its measurements.",
            "funding" => "funding_rate_history has no insert stamp (funding_time is the payment time), so rows cannot be matched to a run. The items counter above is the source of truth.",
            "discovery" => "No instrument re-confirmations landed inside this run's window.",
            _ => "No bars were written or repaired inside this run's window.",
        };

        return new RunDetails(
            r.SegmentCode,
            new CollectorRunRow(r.Id, r.Collector, r.StartedAt, r.DurationMs, r.Ok, r.Error, r.Items),
            caption, total, rows, emptyNote);
    }
}

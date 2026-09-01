using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// The integrations registry. Lifecycle columns (status, name, description) are the admin's to
/// write; everything observed — collector state, durations, instrument counts — is read-only here
/// and always derived, never stored on the exchange row.
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
                   (select count(*)::int from exchange_instrument i
                     where i.exchange_code = e.code and i.status = 'trading')          as "TradingInstruments",
                   (select count(*)::int from exchange_instrument i
                     where i.exchange_code = e.code)                                   as "KnownInstruments",
                   (select max(s.consecutive_failures) from collector_status s
                     where s.exchange_code = e.code)                                   as "MaxFailures",
                   (select avg(s.avg_duration_ms) from collector_status s
                     where s.exchange_code = e.code and s.avg_duration_ms is not null) as "AvgDurationMs",
                   (select extract(epoch from now() - max(s.last_success_at))::double precision
                      from collector_status s
                     where s.exchange_code = e.code and s.collector = 'discovery')     as "DiscoveryAgeSeconds"
              from exchange e
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
            select e.code        as "Code",
                   e.name        as "Name",
                   e.status      as "Status",
                   e.description as "Description",
                   (select count(*)::int from exchange_instrument i
                     where i.exchange_code = e.code and i.status = 'trading') as "TradingInstruments",
                   (select count(*)::int from exchange_instrument i
                     where i.exchange_code = e.code)                          as "KnownInstruments",
                   null::int                                                  as "MaxFailures",
                   null::double precision                                     as "AvgDurationMs",
                   null::double precision                                     as "DiscoveryAgeSeconds"
              from exchange e
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
             where s.exchange_code = @code
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
             where i.exchange_code = @code and i.status = 'trading'
             order by l.received_at asc limit 6
            """,
            new { code },
            cancellationToken: ct))).ToList();

        // Snapshot throughput: rows per 5 min over 2 h, for the detail chart.
        var throughput = (await conn.QueryAsync<double>(new CommandDefinition(
            """
            with buckets as (select generate_series(date_trunc('hour', now()) - interval '2 hours', now(), interval '5 minutes') as b)
            select count(m.received_at)::double precision
              from buckets
              left join market_snapshot m on m.received_at >= buckets.b and m.received_at < buckets.b + interval '5 minutes'
               and m.exchange_instrument_id in (select id from exchange_instrument where exchange_code = @code)
             group by buckets.b order by buckets.b
            """,
            new { code },
            cancellationToken: ct))).ToList();

        var config = await conn.QuerySingleAsync<ExchangeConfigRow>(new CommandDefinition(
            """
            select adapter                as "Adapter",
                   base_url               as "BaseUrl",
                   charts_url             as "ChartsUrl",
                   quote_assets           as "QuoteAssets",
                   blacklist              as "Blacklist",
                   snapshot_interval_s    as "SnapshotIntervalS",
                   candle_interval_s      as "CandleIntervalS",
                   discovery_interval_min as "DiscoveryIntervalMin",
                   funding_interval_min   as "FundingIntervalMin",
                   depth_interval_s       as "DepthIntervalS",
                   updated_by             as "UpdatedBy"
              from exchange
             where code = @code
            """,
            new { code },
            cancellationToken: ct));

        // Global interval values, to show as placeholders where an override is empty.
        var globals = (await conn.QueryAsync<(string Key, int Value)>(new CommandDefinition(
            """
            select key, value::int from setting
             where key in ('snapshot_interval_s','candle_interval_s','discovery_interval_min',
                           'funding_interval_min','depth_interval_s')
            """,
            cancellationToken: ct)))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        return new ExchangeDetails(exchange, config, globals, collectors, stalest, throughput, await RunStore.LatencyAsync(conn, code, ct));
    }

    private static readonly string[] AllowedStatuses =
        ["planned", "enabled", "disabled", "maintenance", "abandoned"];

    /// <summary>
    /// Writes the editable configuration of an exchange, stamping who did it. Status is NOT written
    /// here — it is the guarded control on the overview (see <see cref="SetStatusAsync"/>) — so this
    /// form can never take a venue offline by accident. Adapter is bound to the code and read-only.
    /// Interval values are per-exchange overrides; null means "use the global setting".
    /// </summary>
    public static async Task<bool> SaveAsync(DbConnection conn, ExchangeSaveInput input, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            update exchange
               set name                   = @Name,
                   description            = nullif(@Description, ''),
                   base_url               = nullif(@BaseUrl, ''),
                   charts_url             = nullif(@ChartsUrl, ''),
                   quote_assets           = @QuoteAssets,
                   blacklist              = @Blacklist,
                   snapshot_interval_s    = @SnapshotIntervalS,
                   candle_interval_s      = @CandleIntervalS,
                   discovery_interval_min = @DiscoveryIntervalMin,
                   funding_interval_min   = @FundingIntervalMin,
                   depth_interval_s       = @DepthIntervalS,
                   updated_by             = @UpdatedBy,
                   updated_at             = now()
             where code = @Code
            """,
            input,
            cancellationToken: ct));
        return rows == 1;
    }

    /// <summary>
    /// The guarded status change — the only control that stops (or starts) collection. Returns false
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
            "update exchange set status = @status, updated_by = @updatedBy, updated_at = now() where code = @code",
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
             where exchange_code = @code and (@collector is null or collector = @collector)
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
             where exchange_code = @code and started_at > now() - interval '12 hours' and ok
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
        var run = await conn.QuerySingleOrDefaultAsync<(string ExchangeCode, long Id, string Collector, DateTime StartedAt, int DurationMs, bool Ok, string? Error, int? Items)?>(
            new CommandDefinition(
                """
                select exchange_code as "ExchangeCode", id as "Id", collector as "Collector",
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
        var winStart = r.StartedAt;
        var winEnd = r.StartedAt.AddMilliseconds(r.DurationMs).AddSeconds(2);

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
                     where i.exchange_code = @code and m.received_at >= @winStart and m.received_at < @winEnd
                    union all
                    select i.exchange_symbol, 'last ' || round(l.last_price::numeric, 4), l.received_at
                      from market_snapshot_latest l join exchange_instrument i on i.id = l.exchange_instrument_id
                     where i.exchange_code = @code and l.received_at >= @winStart and l.received_at < @winEnd
                ) w order by "When" desc, "Symbol"
                """),
            "depth" => ("latest-snapshot depth measurements stamped inside the window",
                """
                select i.exchange_symbol as "Symbol", 'depth25 ' || coalesce(round(l.depth_bid_25bps::numeric, 0)::text, '—') as "What", l.depth_at as "When"
                  from market_snapshot_latest l join exchange_instrument i on i.id = l.exchange_instrument_id
                 where i.exchange_code = @code and l.depth_at >= @winStart and l.depth_at < @winEnd
                 order by l.depth_at desc
                """),
            "discovery" => ("instruments confirmed by this discovery pass",
                """
                select i.exchange_symbol as "Symbol", i.status as "What", i.last_seen_at as "When"
                  from exchange_instrument i
                 where i.exchange_code = @code and i.last_seen_at >= @winStart and i.last_seen_at < @winEnd
                 order by i.exchange_symbol
                """),
            "candles" => ("1m bars written or repaired inside the window",
                """
                select i.exchange_symbol as "Symbol", '1m ' || to_char(c.open_time, 'HH24:MI') || ' close ' || round(c.close::numeric, 4) as "What", c.updated_at as "When"
                  from market_candle c join exchange_instrument i on i.id = c.exchange_instrument_id
                 where i.exchange_code = @code and c.timeframe = 1 and c.updated_at >= @winStart and c.updated_at < @winEnd
                 order by c.updated_at desc
                """),
            "rollup" => ("derived bars recomputed inside the window",
                """
                select i.exchange_symbol as "Symbol", c.timeframe || 'm ' || to_char(c.open_time, 'HH24:MI') as "What", c.updated_at as "When"
                  from market_candle c join exchange_instrument i on i.id = c.exchange_instrument_id
                 where i.exchange_code = @code and c.timeframe > 1 and c.updated_at >= @winStart and c.updated_at < @winEnd
                 order by c.updated_at desc
                """),
            _ => ("funding_rate_history has no insert stamp (funding_time is the payment time) — the run's items counter is the source of truth here",
                "select null::text as \"Symbol\", null::text as \"What\", null::timestamptz as \"When\" where false"),
        };

        var all = (await conn.QueryAsync<RunDataRow>(new CommandDefinition(
            sql, new { code = r.ExchangeCode, winStart, winEnd }, cancellationToken: ct))).ToList();

        return new RunDetails(
            r.ExchangeCode,
            new CollectorRunRow(r.Id, r.Collector, r.StartedAt, r.DurationMs, r.Ok, r.Error, r.Items),
            caption, all.Count, all.Take(40).ToList());
    }
}

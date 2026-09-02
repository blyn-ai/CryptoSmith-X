using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// The status dashboard, assembled from the tables that already exist. Health is observed and
/// computed here on every render, never stored. Where a panel has no rows on a fresh stack it
/// simply renders empty — that is the honest state of a new deployment.
/// </summary>
public static class DashboardStore
{
    // A snapshot older than three intervals means the feed has stopped; three failures is failing.
    private const int StaleSnapshotSeconds = 30;   // 3 × 10 s snapshot interval

    public static async Task<Dashboard> LoadAsync(DbConnection conn, CancellationToken ct)
    {
        var exRows = (await conn.QueryAsync<ExRow>(new CommandDefinition(ExSql, cancellationToken: ct))).ToList();
        var collectors = (await conn.QueryAsync<ColRow>(new CommandDefinition(ColSql, cancellationToken: ct))).ToList();
        var bots = (await conn.QueryAsync<BotRow>(new CommandDefinition(BotSql, cancellationToken: ct))).ToList();
        var events = (await conn.QueryAsync<EvRow>(new CommandDefinition(EvSql, cancellationToken: ct))).ToList();
        var tenants = (await conn.QueryAsync<TenRow>(new CommandDefinition(TenSql, cancellationToken: ct))).ToList();
        var buckets = (await conn.QueryAsync<double>(new CommandDefinition(IngestSql, cancellationToken: ct))).ToList();

        // Per-exchange sparkline: snapshot rows per 5 min for the last 2 h.
        var sparkByExchange = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var row in await conn.QueryAsync<SparkRow>(new CommandDefinition(SparkSql, cancellationToken: ct)))
        {
            if (!sparkByExchange.TryGetValue(row.ExchangeCode, out var list))
            {
                sparkByExchange[row.ExchangeCode] = list = [];
            }

            list.Add(row.Rows);
        }

        var exchanges = exRows.Select(e =>
        {
            var health = HealthOf(e, collectors);
            sparkByExchange.TryGetValue(e.Code, out var sp);
            return new DashExchange(e.Code, e.Name, e.Status, health,
                e.TradingInstruments, e.KnownInstruments, e.WorstAgeSeconds,
                e.Status == "enabled" ? (sp ?? []) : []);
        }).ToList();

        var collectorVms = collectors.Select(c => new DashCollector(
            c.ExchangeCode, c.Collector, c.LastSuccessAgeSeconds, c.ConsecutiveFailures,
            c.AvgDurationMs, c.LastError, CollectorHealth(c))).ToList();

        var botVms = bots.Select(b => new DashBot(
            b.Id, b.TenantCode, b.BotInstanceId, b.LastHeartbeatAgeSeconds,
            b.LastHeartbeatAgeSeconds is not null and < 180)).ToList();

        var enabled = exchanges.Where(e => e.Status == "enabled").ToList();
        var failing = enabled.Count(e => e.Health == "failing");
        var degraded = enabled.Count(e => e.Health == "degraded");
        var botsOnline = botVms.Count(b => b.Online);
        var silent = botVms.FirstOrDefault(b => !b.Online && b.LastHeartbeatAgeSeconds is not null);

        return new Dashboard(
            ExchangesEnabled: enabled.Count,
            ExchangesTotal: exchanges.Count,
            ExchangesMaintenance: exchanges.Count(e => e.Status == "maintenance"),
            ExchangesPlanned: exchanges.Count(e => e.Status == "planned"),
            CollectorsOk: collectorVms.Count(c => c.Health == "ok"),
            CollectorsTotal: collectorVms.Count,
            CollectorsFailing: collectorVms.Count(c => c.Health == "fail"),
            InstrumentsTrading: exchanges.Sum(e => e.TradingInstruments),
            InstrumentsKnown: exchanges.Sum(e => e.KnownInstruments),
            BotsOnline: botsOnline,
            BotsTotal: botVms.Count,
            SilentBotNote: silent is null ? null : $"{silent.TenantCode} · {silent.BotInstanceId} silent {Format.Age(silent.LastHeartbeatAgeSeconds)}",
            EventsLastHour: (int)buckets.Sum(),
            Failing: failing,
            Degraded: degraded,
            Verdict: Verdict(failing, degraded, exchanges),
            Exchanges: exchanges,
            Collectors: collectorVms,
            Bots: botVms,
            Events: events.Select(e => new DashEvent(e.Utc, e.Type, e.TenantCode, e.BotId, e.BotInstanceId, IsError(e.Type))).ToList(),
            Tenants: tenants.Select(t => new DashTenant(t.Code, t.BotCount, t.CreatedAt)).ToList(),
            IngestBuckets: buckets,
            IngestPeak: buckets.Count == 0 ? 0 : (int)buckets.Max(),
            AsOf: DateTime.UtcNow);
    }

    private static string HealthOf(ExRow e, IReadOnlyList<ColRow> collectors)
    {
        if (e.Status == "maintenance")
        {
            return "paused";
        }

        if (e.Status != "enabled")
        {
            return "none";
        }

        // Interval-blind: consecutive_failures is maintained by the collector loop and already
        // knows each collector's cadence. Absolute last-success age is not comparable across a
        // 10 s snapshot and a 60 min discovery, so it is not used for severity. Staleness is judged
        // from the actual snapshot age instead.
        var mine = collectors.Where(c => c.ExchangeCode == e.Code).ToList();
        if (mine.Any(c => c.ConsecutiveFailures >= 3))
        {
            return "failing";
        }

        if (mine.Any(c => c.ConsecutiveFailures >= 1) || e.WorstAgeSeconds > StaleSnapshotSeconds)
        {
            return "degraded";
        }

        return "ok";
    }

    private static string CollectorHealth(ColRow c) =>
        c.ConsecutiveFailures >= 3 ? "fail"
        : c.ConsecutiveFailures >= 1 ? "warn"
        : "ok";

    private static bool IsError(string type) =>
        type.Contains("error", StringComparison.OrdinalIgnoreCase)
        || type.Contains("blocked", StringComparison.OrdinalIgnoreCase)
        || type.Contains("reject", StringComparison.OrdinalIgnoreCase);

    private static string Verdict(int failing, int degraded, IReadOnlyList<DashExchange> ex)
    {
        if (failing == 0 && degraded == 0)
        {
            return "Every enabled exchange is inside its interval.";
        }

        var parts = new List<string>();
        foreach (var e in ex.Where(e => e.Health is "failing"))
        {
            parts.Add($"{e.Code} has not collected for {Format.Age(e.WorstAgeSeconds)}");
        }

        foreach (var e in ex.Where(e => e.Health is "degraded"))
        {
            parts.Add($"{e.Code} is degraded");
        }

        return string.Join(". ", parts) + ".";
    }

    // Worst (oldest) snapshot age across an exchange's trading instruments feeds the degraded rule.
    private const string ExSql =
        """
        select e.code as "Code", e.name as "Name", e.status as "Status",
               (select count(*)::int from exchange_instrument i where i.exchange_code = e.code and i.status = 'trading') as "TradingInstruments",
               (select count(*)::int from exchange_instrument i where i.exchange_code = e.code) as "KnownInstruments",
               (select extract(epoch from now() - min(l.received_at))::double precision
                  from market_snapshot_latest l join exchange_instrument i on i.id = l.exchange_instrument_id
                 where i.exchange_code = e.code and i.status = 'trading') as "WorstAgeSeconds"
          from exchange e
         order by case e.status when 'enabled' then 0 when 'maintenance' then 1 when 'disabled' then 2 when 'planned' then 3 else 4 end, e.code
        """;

    private const string ColSql =
        """
        select s.exchange_code as "ExchangeCode", s.collector as "Collector",
               extract(epoch from now() - s.last_success_at)::double precision as "LastSuccessAgeSeconds",
               s.consecutive_failures as "ConsecutiveFailures",
               s.avg_duration_ms::int as "AvgDurationMs", s.last_error as "LastError"
          from collector_status s
         order by s.exchange_code, s.collector
        """;

    private const string BotSql =
        """
        select b.id as "Id", b.tenant_code as "TenantCode", b.bot_instance_id as "BotInstanceId",
               extract(epoch from now() - b.last_heartbeat_at)::double precision as "LastHeartbeatAgeSeconds"
          from bot b order by b.last_heartbeat_at desc nulls last, b.bot_instance_id
        """;

    private const string EvSql =
        """
        select e.utc as "Utc", e.type as "Type", b.tenant_code as "TenantCode", b.id as "BotId", b.bot_instance_id as "BotInstanceId"
          from bot_event e join bot b on b.id = e.bot_id
         order by e.received_at desc limit 8
        """;

    private const string TenSql =
        """
        select t.code as "Code",
               (select count(*)::int from bot b where b.tenant_code = t.code) as "BotCount",
               t.created_at as "CreatedAt"
          from tenant t order by t.code
        """;

    // 15-min buckets over the last 6 h, zero-filled so the sparkline has a continuous x-axis.
    private const string IngestSql =
        """
        with buckets as (
            select generate_series(date_trunc('hour', now()) - interval '6 hours',
                                   date_trunc('hour', now()) + interval '1 hour',
                                   interval '15 minutes') as b
        )
        select count(e.received_at)::double precision
          from buckets
          left join bot_event e on e.received_at >= buckets.b and e.received_at < buckets.b + interval '15 minutes'
         where buckets.b <= now()
         group by buckets.b order by buckets.b
        """;

    // 5-min buckets over 2 h per exchange, for the row sparklines.
    // Ingest volume per exchange, read from the run log rather than from the snapshot rows
    // themselves. Counting market_snapshot re-scanned ~174k rows per dashboard render (three
    // venues x 25 buckets, each its own pass) and grew with the archive — on a live refresh
    // every 10 s that was the single heaviest thing the console did, and it starved the very
    // writers it was measuring. collector_run already carries items per pass: same shape,
    // 25 ms instead of seconds, and the cost now scales with passes, not with history.
    private const string SparkSql =
        """
        with buckets as (
            select generate_series(date_trunc('hour', now()) - interval '2 hours', now(), interval '5 minutes') as b
        ),
        ex as (select code from exchange where status = 'enabled'),
        agg as (
            select r.exchange_code as code,
                   to_timestamp(floor(extract(epoch from r.started_at) / 300) * 300) as b,
                   sum(r.items)::double precision as rows
              from collector_run r
             where r.collector = 'snapshot' and r.ok
               and r.started_at >= date_trunc('hour', now()) - interval '2 hours'
             group by 1, 2
        )
        select ex.code as "ExchangeCode", coalesce(agg.rows, 0) as "Rows"
          from ex cross join buckets
          left join agg on agg.code = ex.code and agg.b = buckets.b
         order by ex.code, buckets.b
        """;

    private sealed record ExRow(string Code, string Name, string Status, int TradingInstruments, int KnownInstruments, double? WorstAgeSeconds);
    private sealed record ColRow(string ExchangeCode, string Collector, double? LastSuccessAgeSeconds, int ConsecutiveFailures, int? AvgDurationMs, string? LastError);
    private sealed record BotRow(int Id, string TenantCode, string BotInstanceId, double? LastHeartbeatAgeSeconds);
    private sealed record EvRow(DateTime Utc, string Type, string TenantCode, int BotId, string BotInstanceId);
    private sealed record TenRow(string Code, int BotCount, DateTime CreatedAt);
    private sealed record SparkRow(string ExchangeCode, double Rows);
}

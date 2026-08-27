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
            select i.exchange_symbol as "Symbol",
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

        return new ExchangeDetails(exchange, collectors, stalest, throughput);
    }

    /// <summary>Returns false when the code does not exist or the status is not an allowed value.</summary>
    public static async Task<bool> SaveSettingsAsync(
        DbConnection conn, string code, string name, string status, string? description, CancellationToken ct)
    {
        // The CHECK constraint is the real guard; this keeps a typo from becoming a 500.
        if (status is not ("planned" or "enabled" or "disabled" or "maintenance" or "abandoned"))
        {
            return false;
        }

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            update exchange
               set name        = @name,
                   status      = @status,
                   description = nullif(@description, ''),
                   updated_at  = now()
             where code = @code
            """,
            new { code, name, status, description },
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

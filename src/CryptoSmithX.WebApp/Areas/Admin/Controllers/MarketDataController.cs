using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// The read-only market-data console the marketdata scaffold used to serve. It reads the marketdata
/// tables directly (WebApp does not reference the Api); the SQL is copied from
/// <c>MarketData.Api/Endpoints.cs</c>, not reinvented. Nothing here writes.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class MarketDataController : Controller
{
    private readonly Db _db;
    private readonly IConfiguration _config;

    public MarketDataController(Db db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Same staleness window as the Api: three snapshot intervals.
        var snapshotIntervalSeconds = _config.GetValue<int?>("MarketData:SnapshotIntervalSeconds") ?? 10;
        var staleSeconds = snapshotIntervalSeconds * 3.0;

        await using var conn = await _db.OpenAsync(ct);

        var collectors = (await conn.QueryAsync(new CommandDefinition(
            """
            select s.exchange_code        as "exchangeCode",
                   s.collector            as "collector",
                   s.last_attempt_at      as "lastAttemptAt",
                   s.last_success_at      as "lastSuccessAt",
                   extract(epoch from now() - s.last_success_at)::double precision as "lastSuccessAgeSeconds",
                   s.consecutive_failures as "consecutiveFailures",
                   s.last_error           as "lastError",
                   s.instruments_expected as "instrumentsExpected"
              from collector_status s
             order by s.exchange_code, s.collector
            """, cancellationToken: ct))).ToList();

        var stale = (await conn.QueryAsync(new CommandDefinition(
            """
            select i.exchange_code   as "exchangeCode",
                   i.exchange_symbol as "symbol",
                   l.received_at     as "receivedAt",
                   extract(epoch from now() - l.received_at)::double precision as "ageSeconds"
              from exchange_instrument i
              left join market_snapshot_latest l on l.exchange_instrument_id = i.id
             where i.status = 'trading'
               and (l.received_at is null or l.received_at < now() - make_interval(secs => @staleSeconds))
             order by l.received_at nulls first
             limit 200
            """,
            new { staleSeconds },
            cancellationToken: ct))).ToList();

        var instruments = (await conn.QueryAsync(new CommandDefinition(
            """
            select exchange_code   as "exchangeCode",
                   exchange_symbol as "symbol",
                   base_asset      as "baseAsset",
                   quote_asset     as "quoteAsset",
                   status,
                   last_seen_at    as "lastSeenAt"
              from exchange_instrument
             order by exchange_code, exchange_symbol
            """, cancellationToken: ct))).ToList();

        var snapshot = (await conn.QueryAsync(new CommandDefinition(
            """
            select i.exchange_code   as "exchangeCode",
                   i.exchange_symbol as "symbol",
                   l.received_at     as "receivedAt",
                   extract(epoch from now() - l.received_at)::double precision as "ageSeconds",
                   l.last_price      as "lastPrice",
                   l.bid_price       as "bidPrice",
                   l.ask_price       as "askPrice",
                   case when (l.bid_price + l.ask_price) > 0
                        then (l.ask_price - l.bid_price) / ((l.bid_price + l.ask_price) / 2) * 10000
                   end               as "spreadBps",
                   l.mark_price      as "markPrice",
                   l.funding_rate    as "fundingRate",
                   l.open_interest * l.mark_price as "openInterestNotional"
              from market_snapshot_latest l
              join exchange_instrument i on i.id = l.exchange_instrument_id
             order by i.exchange_symbol
            """, cancellationToken: ct))).ToList();

        return View(new MarketDataConsole(collectors, stale, instruments, snapshot, DateTimeOffset.UtcNow));
    }
}

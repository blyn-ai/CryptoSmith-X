using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Api;

/// <summary>
/// The read-only surface. Every endpoint reads tables; none of them looks at a collector's memory,
/// so the API answers the same whether it runs beside the collectors or not. Values that can be
/// derived — spread, OI notional, ages — are computed here and never stored.
/// </summary>
public static class Endpoints
{
    public static void MapMarketDataApi(this WebApplication app)
    {
        var api = app.MapGroup("/v1");

        api.MapGet("/health", Health);
        api.MapGet("/exchanges", Exchanges);
        api.MapGet("/instruments", Instruments);
        api.MapGet("/snapshot", Snapshot);
        api.MapGet("/candles", Candles);
    }

    private static async Task<IResult> Health(Db db, IConfiguration config, CancellationToken ct)
    {
        // The only value /health needs from the Hub's world. Read straight from configuration so the
        // Api owns no shared options type and does not reference the Hub. Default matches the Hub's.
        var snapshotIntervalSeconds = config.GetValue<int?>("MarketData:SnapshotIntervalSeconds") ?? 10;
        await using var conn = await db.OpenAsync(ct);

        var collectors = (await conn.QueryAsync<CollectorRow>(new CommandDefinition(
            """
            select s.exchange_code                                       as "ExchangeCode",
                   s.collector                                           as "Collector",
                   s.last_attempt_at                                     as "LastAttemptAt",
                   s.last_success_at                                     as "LastSuccessAt",
                   extract(epoch from now() - s.last_success_at)::double precision as "LastSuccessAgeSeconds",
                   s.consecutive_failures                                as "ConsecutiveFailures",
                   s.last_error                                          as "LastError",
                   extract(epoch from now() - s.last_error_at)::double precision as "LastErrorAgeSeconds",
                   s.instruments_expected                                as "InstrumentsExpected",
                   s.last_duration_ms                                    as "LastDurationMs",
                   s.avg_duration_ms                                     as "AvgDurationMs"
              from collector_status s
             order by s.exchange_code, s.collector
            """, cancellationToken: ct))).ToList();

        // A trading instrument whose latest snapshot is older than three intervals is not being
        // updated, even though its collector may look fine.
        var staleSeconds = snapshotIntervalSeconds * 3.0;
        var stale = (await conn.QueryAsync<StaleRow>(new CommandDefinition(
            """
            select i.exchange_code                                    as "ExchangeCode",
                   i.exchange_symbol                                  as "Symbol",
                   l.received_at                                      as "ReceivedAt",
                   extract(epoch from now() - l.received_at)::double precision as "AgeSeconds"
              from exchange_instrument i
              left join market_snapshot_latest l on l.exchange_instrument_id = i.id
             where i.status = 'trading'
               and (l.received_at is null or l.received_at < now() - make_interval(secs => @staleSeconds))
             order by l.received_at nulls first
             limit 200
            """,
            new { staleSeconds },
            cancellationToken: ct))).ToList();

        var degraded = collectors.Count == 0
            || collectors.Any(c => c.ConsecutiveFailures > 0 || c.LastSuccessAt is null)
            || stale.Count > 0;

        return Results.Ok(new
        {
            status = degraded ? "degraded" : "ok",
            asOf = DateTimeOffset.UtcNow,
            collectors,
            staleInstruments = stale,
        });
    }

    private static async Task<IResult> Exchanges(Db db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            select e.code, e.name, e.status, e.description,
                   (select count(*) from exchange_instrument i
                     where i.exchange_code = e.code and i.status = 'trading') as "tradingInstruments",
                   (select count(*) from exchange_instrument i
                     where i.exchange_code = e.code) as "knownInstruments"
              from exchange e
             order by e.code
            """, cancellationToken: ct));
        return Results.Ok(rows);
    }

    private static async Task<IResult> Instruments(Db db, string? exchange, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            select exchange_code          as "exchangeCode",
                   exchange_symbol        as symbol,
                   base_asset             as "baseAsset",
                   quote_asset            as "quoteAsset",
                   contract_multiplier    as "contractMultiplier",
                   price_step             as "priceStep",
                   qty_step               as "qtyStep",
                   min_qty                as "minQty",
                   min_notional           as "minNotional",
                   funding_interval_hours as "fundingIntervalHours",
                   status,
                   status_changed_at      as "statusChangedAt",
                   first_seen_at          as "firstSeenAt",
                   last_seen_at           as "lastSeenAt"
              from exchange_instrument
             where (@exchange is null or exchange_code = @exchange)
             order by exchange_code, exchange_symbol
            """,
            new { exchange },
            cancellationToken: ct));
        return Results.Ok(rows);
    }

    private static async Task<IResult> Snapshot(Db db, string? exchange, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync(new CommandDefinition(
            """
            select i.exchange_code   as "exchangeCode",
                   i.exchange_symbol as symbol,
                   i.base_asset      as "baseAsset",
                   i.quote_asset     as "quoteAsset",
                   l.received_at     as "receivedAt",
                   extract(epoch from now() - l.received_at)::double precision as "ageSeconds",
                   l.last_price      as "lastPrice",
                   l.bid_price       as "bidPrice",
                   l.ask_price       as "askPrice",
                   l.bid_size        as "bidSize",
                   l.ask_size        as "askSize",
                   -- derived, never stored
                   case when (l.bid_price + l.ask_price) > 0
                        then (l.ask_price - l.bid_price) / ((l.bid_price + l.ask_price) / 2) * 10000
                   end               as "spreadBps",
                   l.mark_price      as "markPrice",
                   l.index_price     as "indexPrice",
                   l.funding_rate    as "fundingRate",
                   l.turnover_24h    as "turnover24h",
                   l.open_interest   as "openInterest",
                   l.open_interest * l.mark_price as "openInterestNotional",
                   l.open_interest_at as "openInterestAt",
                   l.depth_bid_10bps as "depthBid10Bps",
                   l.depth_ask_10bps as "depthAsk10Bps",
                   l.depth_bid_25bps as "depthBid25Bps",
                   l.depth_ask_25bps as "depthAsk25Bps",
                   l.depth_bid_50bps as "depthBid50Bps",
                   l.depth_ask_50bps as "depthAsk50Bps",
                   l.depth_at        as "depthAt"
              from market_snapshot_latest l
              join exchange_instrument i on i.id = l.exchange_instrument_id
             where (@exchange is null or i.exchange_code = @exchange)
             order by i.exchange_symbol
            """,
            new { exchange },
            cancellationToken: ct))).ToList();

        var asOf = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            select max(l.received_at)
              from market_snapshot_latest l
              join exchange_instrument i on i.id = l.exchange_instrument_id
             where (@exchange is null or i.exchange_code = @exchange)
            """,
            new { exchange },
            cancellationToken: ct));

        return Results.Ok(new { asOf, tickers = rows });
    }

    private static async Task<IResult> Candles(
        Db db, string exchange, string symbol, int tf, int? limit, CancellationToken ct)
    {
        if (tf <= 0)
        {
            return Results.BadRequest(new { error = "tf must be a positive number of minutes." });
        }

        var take = Math.Clamp(limit ?? 300, 1, 5000);

        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync(new CommandDefinition(
            """
            select c.open_time   as "openTime",
                   c.open, c.high, c.low, c.close, c.volume,
                   c.trade_count as "tradeCount",
                   c.bar_count   as "barCount",
                   c.updated_at  as "updatedAt"
              from market_candle c
              join exchange_instrument i on i.id = c.exchange_instrument_id
             where i.exchange_code = @exchange
               and i.exchange_symbol = @symbol
               and c.timeframe = @tf
             order by c.open_time desc
             limit @take
            """,
            new { exchange, symbol, tf = (short)tf, take },
            cancellationToken: ct))).Reverse();   // newest last

        return Results.Ok(new { exchange, symbol, timeframe = tf, candles = rows });
    }

    // timestamptz comes back from Npgsql as DateTime with Kind=Utc, so that is what these say.
    private sealed record CollectorRow(
        string ExchangeCode,
        string Collector,
        DateTime LastAttemptAt,
        DateTime? LastSuccessAt,
        double? LastSuccessAgeSeconds,
        int ConsecutiveFailures,
        string? LastError,
        double? LastErrorAgeSeconds,
        int? InstrumentsExpected,
        int? LastDurationMs,
        double? AvgDurationMs);

    private sealed record StaleRow(
        string ExchangeCode,
        string Symbol,
        DateTime? ReceivedAt,
        double? AgeSeconds);
}

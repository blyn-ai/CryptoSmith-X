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
        api.MapGet("/as-of", AsOf);
        api.MapGet("/coverage", Coverage);
    }

    private static async Task<IResult> Health(Db db, IConfiguration config, CancellationToken ct)
    {
        // The only value /health needs from the Hub's world. Read straight from configuration so the
        // Api owns no shared options type and does not reference the Hub. Default matches the Hub's.
        var snapshotIntervalSeconds = config.GetValue<int?>("MarketData:SnapshotIntervalSeconds") ?? 10;
        await using var conn = await db.OpenAsync(ct);

        var collectors = (await conn.QueryAsync<CollectorRow>(new CommandDefinition(
            """
            select s.segment_code                                       as "SegmentCode",
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
             order by s.segment_code, s.collector
            """, cancellationToken: ct))).ToList();

        // A trading instrument whose latest snapshot is older than three intervals is not being
        // updated, even though its collector may look fine.
        var staleSeconds = snapshotIntervalSeconds * 3.0;
        var stale = (await conn.QueryAsync<StaleRow>(new CommandDefinition(
            """
            select i.segment_code                                    as "SegmentCode",
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
                     where i.segment_code = e.code and i.status = 'trading') as "tradingInstruments",
                   (select count(*) from exchange_instrument i
                     where i.segment_code = e.code) as "knownInstruments"
              from segment e
             order by e.code
            """, cancellationToken: ct));
        return Results.Ok(rows);
    }

    // The public query parameter stays `exchange` even though 0019 renamed the column to
    // `segment_code` and its value is a segment code (`kraken-futures`, not `kraken`). Renaming a
    // published /v1 parameter breaks every caller; that belongs in a version bump, not in a
    // schema migration. Same reason the parameter keeps its name in the three reads below.
    private static async Task<IResult> Instruments(Db db, string? exchange, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            select segment_code          as "segmentCode",
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
             where (@exchange is null or segment_code = @exchange)
             order by segment_code, exchange_symbol
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
            select i.segment_code   as "segmentCode",
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
             where (@exchange is null or i.segment_code = @exchange)
             order by i.exchange_symbol
            """,
            new { exchange },
            cancellationToken: ct))).ToList();

        var asOf = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            select max(l.received_at)
              from market_snapshot_latest l
              join exchange_instrument i on i.id = l.exchange_instrument_id
             where (@exchange is null or i.segment_code = @exchange)
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
             where i.segment_code = @exchange
               and i.exchange_symbol = @symbol
               and c.timeframe = @tf
             order by c.open_time desc
             limit @take
            """,
            new { exchange, symbol, tf = (short)tf, take },
            cancellationToken: ct))).Reverse();   // newest last

        return Results.Ok(new { exchange, symbol, timeframe = tf, candles = rows });
    }


    /// <summary>
    /// One instrument as it stood at an instant: the newest observation whose <c>received_at</c> is
    /// at or before <c>at</c>. Reads market_snapshot, the history, not market_snapshot_latest.
    ///
    /// The answer always carries <c>lagSeconds</c> — how far BEFORE the asked instant the returned
    /// observation was actually written. Without it the caller cannot tell a price measured a second
    /// before their instant from one measured an hour before it, and the endpoint would be handing
    /// back a stale figure dressed as a fresh one. Same reason the admin's cross-venue page prints a
    /// signed lag beside every number instead of a rounded age.
    /// </summary>
    private static async Task<IResult> AsOf(
        Db db, string exchange, string symbol, DateTimeOffset? at, CancellationToken ct)
    {
        if (at is not { } instant)
        {
            return Results.BadRequest(new { error = "at is required, as an ISO-8601 instant. Without it this is /v1/snapshot." });
        }

        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            """
            select i.segment_code    as "segmentCode",
                   i.exchange_symbol  as symbol,
                   i.base_asset       as "baseAsset",
                   i.quote_asset      as "quoteAsset",
                   s.received_at      as "receivedAt",
                   extract(epoch from (@at - s.received_at))::double precision as "lagSeconds",
                   s.last_price       as "lastPrice",
                   s.bid_price        as "bidPrice",
                   s.ask_price        as "askPrice",
                   s.bid_size         as "bidSize",
                   s.ask_size         as "askSize",
                   case when (s.bid_price + s.ask_price) > 0
                        then (s.ask_price - s.bid_price) / ((s.bid_price + s.ask_price) / 2) * 10000
                   end                as "spreadBps",
                   s.mark_price       as "markPrice",
                   s.index_price      as "indexPrice",
                   s.funding_rate     as "fundingRate",
                   s.turnover_24h     as "turnover24h",
                   s.open_interest    as "openInterest",
                   s.open_interest * s.mark_price as "openInterestNotional",
                   s.open_interest_at as "openInterestAt",
                   s.depth_bid_10bps  as "depthBid10Bps",
                   s.depth_ask_10bps  as "depthAsk10Bps",
                   s.depth_bid_25bps  as "depthBid25Bps",
                   s.depth_ask_25bps  as "depthAsk25Bps",
                   s.depth_bid_50bps  as "depthBid50Bps",
                   s.depth_ask_50bps  as "depthAsk50Bps",
                   s.depth_at         as "depthAt"
              from market_snapshot s
              join exchange_instrument i on i.id = s.exchange_instrument_id
             where i.segment_code = @exchange
               and i.exchange_symbol = @symbol
               and s.received_at <= @at
             order by s.received_at desc
             limit 1
            """,
            new { exchange, symbol, at = instant.UtcDateTime },
            cancellationToken: ct));

        // Nothing observed at or before the instant is not an empty result, it is a different
        // sentence: either we do not carry that instrument or we were not collecting it yet.
        return row is null
            ? Results.NotFound(new
            {
                error = "No observation at or before that instant.",
                exchange, symbol, at = instant,
            })
            : Results.Ok(new { at = instant, observation = row });
    }

    /// <summary>
    /// What this deployment actually holds: one row per venue, plus totals. Written for the caller
    /// deciding whether to bother — it answers "which venues, how many instruments, since when, and
    /// what kinds of data" in a single request and reads no market data at all.
    ///
    /// Three instrument counts rather than one, because since 0029 they are three different numbers
    /// and the difference is large: a venue can list a thousand contracts, trade 980 of them, and be
    /// collected by us on 25. <c>instruments</c> is the collected count — the one a caller can
    /// actually fetch data for — and the venue's own two stand beside it under their own names.
    ///
    /// <c>datasets</c> lists the collecting datasets a caller can see the output of, so discovery
    /// and rollup are left out: both are internal machinery, not something served. Open interest has
    /// no entry either and that is not an omission — 0014 disables its loop on every venue with the
    /// note that it rides the snapshot ticker, so it arrives inside <c>snapshot</c>.
    /// </summary>
    private static async Task<IResult> Coverage(Db db, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var venues = (await conn.QueryAsync<CoverageRow>(new CommandDefinition(
            """
            select s.code                                                       as "Code",
                   s.name                                                        as "Name",
                   s.exchange_code                                               as "ExchangeCode",
                   s.kind                                                        as "Kind",
                   s.status                                                      as "Status",
                   (select min(i.first_seen_at)::date from exchange_instrument i
                     where i.segment_code = s.code)                              as "Since",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = s.code and i.collect)                as "Instruments",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = s.code and i.status = 'trading')     as "Trading",
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = s.code)                              as "Listed",
                   (select coalesce(array_agg(sd.dataset_code order by sd.dataset_code), '{}')
                      from segment_dataset sd
                     where sd.segment_code = s.code
                       and sd.mode <> 'disabled'
                       and sd.dataset_code not in ('discovery', 'rollup'))       as "Datasets"
              from segment s
             where s.status = 'enabled'
             order by s.code
            """, cancellationToken: ct))).ToList();

        return Results.Ok(new
        {
            venues,
            totals = new
            {
                venues = venues.Count,
                instruments = venues.Sum(v => v.Instruments),
                trading = venues.Sum(v => v.Trading),
                listed = venues.Sum(v => v.Listed),
                since = venues.Where(v => v.Since is not null).Select(v => v.Since!.Value).DefaultIfEmpty().Min(),
            },
            measuredAt = DateTimeOffset.UtcNow,
        });
    }

    // timestamptz comes back from Npgsql as DateTime with Kind=Utc, so that is what these say.
    private sealed record CollectorRow(
        string SegmentCode,
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

    private sealed record CoverageRow(
        string Code,
        string Name,
        string ExchangeCode,
        string Kind,
        string Status,
        DateTime? Since,
        int Instruments,
        int Trading,
        int Listed,
        string[] Datasets);

    private sealed record StaleRow(
        string SegmentCode,
        string Symbol,
        DateTime? ReceivedAt,
        double? AgeSeconds);
}

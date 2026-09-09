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
    /// <summary>
    /// Contract specs as the venue states them. Every column here is stored, none is derived.
    ///
    /// <c>include=raw</c> adds the venue's own instrument payload verbatim under <c>raw</c>. That is
    /// deliberately the whole document rather than a hand-picked set of extra fields: max leverage,
    /// margin tiers, settlement currency, expiry and linear-vs-inverse are spelled differently by
    /// every venue and change without warning, so promoting a chosen few to typed columns would
    /// publish a contract that quietly rots. The raw document cannot rot — it is what arrived.
    /// </summary>
    private static async Task<IResult> Instruments(
        Db db, string? exchange, string? status, string? symbols, string? include, CancellationToken ct)
    {
        string[]? wanted = null;
        if (!string.IsNullOrWhiteSpace(symbols)
            && ApiQuery.TryParseSymbols(symbols, null, out var parsed, out _))
        {
            wanted = parsed;
        }

        var withRaw = !string.IsNullOrWhiteSpace(include)
            && ApiQuery.Includes(include, "raw");

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(
            $"""
            select s.exchange_code        as exchange,
                   i.segment_code          as "segmentCode",
                   i.exchange_symbol       as symbol,
                   i.base_asset            as "baseAsset",
                   i.quote_asset           as "quoteAsset",
                   i.contract_multiplier   as "contractMultiplier",
                   i.price_step            as "priceStep",
                   i.qty_step              as "qtyStep",
                   i.min_qty               as "minQty",
                   i.min_notional          as "minNotional",
                   i.funding_interval_hours as "fundingIntervalHours",
                   i.status,
                   i.status_changed_at     as "statusChangedAt",
                   i.listed_at             as "listedAt",
                   i.first_seen_at         as "firstSeenAt",
                   i.last_seen_at          as "lastSeenAt"
                   {(withRaw ? ", i.raw_json as raw" : string.Empty)}
              from exchange_instrument i
              join segment s on s.code = i.segment_code
             where (@exchange is null or i.segment_code = @exchange)
               and (@status is null or i.status = @status)
               and (@symbols is null or i.exchange_symbol = any(@symbols))
             order by i.segment_code, i.exchange_symbol
            """,
            new { exchange, status, symbols = wanted },
            cancellationToken: ct));

        if (!withRaw)
        {
            return Results.Ok(rows);
        }

        // jsonb arrives from Npgsql as a string, and returning it as one would make `raw` a quoted
        // blob the caller has to parse a second time. Re-materialised as an element so the venue's
        // own document is part of this document.
        var materialised = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in (IDictionary<string, object>)row)
            {
                copy[key] = key == "raw" && value is string json
                    ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json)
                    : value;
            }

            materialised.Add(copy);
        }

        return Results.Ok(materialised);
    }

    /// <summary>
    /// The latest stored observation per instrument. Filters narrow which rows come back; none of
    /// them changes what a row means.
    ///
    /// <c>maxAgeSeconds</c> EXCLUDES stale rows rather than flagging them, and says how many it
    /// dropped in <c>warnings</c>. Returning them with an age beside it was the alternative, and it
    /// loses: a caller who sets a freshness bound has already said what they will do with a stale
    /// row, and handing it over anyway invites the check being forgotten one call site later.
    ///
    /// <c>include</c> selects which groups are POPULATED, not which keys exist. The keys stay so the
    /// schema is one shape and so existing callers — who read every field unconditionally — keep
    /// working; a null band already means "not measured" on this endpoint and that reading is
    /// unchanged.
    /// </summary>
    private static async Task<IResult> Snapshot(
        Db db, string? exchange, string? symbols, string? status, double? maxAgeSeconds,
        string? include, CancellationToken ct)
    {
        string[]? wanted = null;
        if (!string.IsNullOrWhiteSpace(symbols))
        {
            if (!ApiQuery.TryParseSymbols(symbols, null, out var parsed, out var symbolError))
            {
                return Results.BadRequest(new { error = symbolError });
            }

            wanted = parsed;
        }

        var withQuote = ApiQuery.Includes(include, "quote");
        var withDepth = ApiQuery.Includes(include, "depth");
        var withInstrument = ApiQuery.Includes(include, "instrument");

        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync(new CommandDefinition(
            """
            select i.segment_code   as "segmentCode",
                   i.exchange_symbol as symbol,
                   case when @withInstrument then i.base_asset  end as "baseAsset",
                   case when @withInstrument then i.quote_asset end as "quoteAsset",
                   l.received_at     as "receivedAt",
                   extract(epoch from now() - l.received_at)::double precision as "ageSeconds",
                   case when @withQuote then l.last_price end as "lastPrice",
                   case when @withQuote then l.bid_price  end as "bidPrice",
                   case when @withQuote then l.ask_price  end as "askPrice",
                   case when @withQuote then l.bid_size   end as "bidSize",
                   case when @withQuote then l.ask_size   end as "askSize",
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
                   case when @withDepth then l.depth_bid_10bps end as "depthBid10Bps",
                   case when @withDepth then l.depth_ask_10bps end as "depthAsk10Bps",
                   case when @withDepth then l.depth_bid_25bps end as "depthBid25Bps",
                   case when @withDepth then l.depth_ask_25bps end as "depthAsk25Bps",
                   case when @withDepth then l.depth_bid_50bps end as "depthBid50Bps",
                   case when @withDepth then l.depth_ask_50bps end as "depthAsk50Bps",
                   case when @withDepth then l.depth_ref       end as "depthRef",
                   l.book_reach_bid  as "bookReachBid",
                   l.book_reach_ask  as "bookReachAsk",
                   l.depth_at        as "depthAt",
                   l.venue_ts        as "venueTs",
                   l.last_trade_at   as "lastTradeAt",
                   l.funding_rate_predicted as "fundingRatePredicted",
                   l.next_funding_at as "nextFundingAt",
                   l.volume_24h_base as "volume24hBase"
              from market_snapshot_latest l
              join exchange_instrument i on i.id = l.exchange_instrument_id
             where (@exchange is null or i.segment_code = @exchange)
               and (@symbols is null or i.exchange_symbol = any(@symbols))
               and (@status is null or i.status = @status)
               and (@maxAgeSeconds is null
                    or l.received_at >= now() - make_interval(secs => @maxAgeSeconds))
             order by i.exchange_symbol
            """,
            new { exchange, symbols = wanted, status, maxAgeSeconds, withQuote, withDepth, withInstrument },
            cancellationToken: ct))).ToList();

        var asOf = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            select max(l.received_at)
              from market_snapshot_latest l
              join exchange_instrument i on i.id = l.exchange_instrument_id
             where (@exchange is null or i.segment_code = @exchange)
               and (@symbols is null or i.exchange_symbol = any(@symbols))
               and (@status is null or i.status = @status)
            """,
            new { exchange, symbols = wanted, status },
            cancellationToken: ct));

        var warnings = new List<string>();
        if (maxAgeSeconds is not null)
        {
            // Counted with the SAME filters minus the age bound, so the number means "excluded for
            // being stale" and not "absent for some other reason the caller also asked for".
            var suppressed = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                select count(*)::int
                  from market_snapshot_latest l
                  join exchange_instrument i on i.id = l.exchange_instrument_id
                 where (@exchange is null or i.segment_code = @exchange)
                   and (@symbols is null or i.exchange_symbol = any(@symbols))
                   and (@status is null or i.status = @status)
                   and l.received_at < now() - make_interval(secs => @maxAgeSeconds)
                """,
                new { exchange, symbols = wanted, status, maxAgeSeconds },
                cancellationToken: ct));

            if (suppressed > 0)
            {
                warnings.Add(
                    $"{suppressed} instruments were left out for being older than {maxAgeSeconds} s. "
                    + "They are still stored; raise maxAgeSeconds or omit it to see them.");
            }
        }

        if (wanted is not null)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                present.Add((string)((IDictionary<string, object>)row)["symbol"]);
            }

            foreach (var s in wanted)
            {
                if (!present.Contains(s))
                {
                    warnings.Add($"{s} returned no row: unknown on this exchange, filtered out, or never observed.");
                }
            }
        }

        var utcAsOf = asOf is { } a ? new DateTimeOffset(DateTime.SpecifyKind(a, DateTimeKind.Utc), TimeSpan.Zero) : (DateTimeOffset?)null;

        return Results.Ok(new
        {
            asOf,
            exchange,
            dataAgeSeconds = utcAsOf is null ? (double?)null : (DateTimeOffset.UtcNow - utcAsOf.Value).TotalSeconds,
            tickers = rows,
            warnings,
        });
    }

    /// <summary>
    /// Bars for one or many instruments. The original call — <c>symbol</c>, <c>tf</c>, <c>limit</c>,
    /// no window — still answers exactly as it did, including the flat <c>candles</c> array and its
    /// newest-last order, because callers depend on it. Everything added is beside that, not instead
    /// of it: <c>symbols</c> for several instruments at once, <c>from</c>/<c>to</c> for a window,
    /// <c>priceType</c> for the mark and index series, and <c>series</c> keyed by symbol.
    ///
    /// <c>from</c>/<c>to</c> stay OPTIONAL here alone. The rule elsewhere is that a historical
    /// request must state its window, but making it mandatory on this endpoint would break every
    /// existing caller, and a contract already published outranks a rule written afterwards.
    /// </summary>
    private static async Task<IResult> Candles(
        Db db, string? exchange, string? symbol, string? symbols, int tf, int? limit,
        DateTimeOffset? from, DateTimeOffset? to, string? priceType, string? cursor,
        CancellationToken ct)
    {
        if (tf <= 0)
        {
            return Results.BadRequest(new { error = "tf must be a positive number of minutes." });
        }

        if (string.IsNullOrWhiteSpace(exchange))
        {
            return Results.BadRequest(new { error = "exchange is required, for example kraken-futures." });
        }

        var series = (priceType ?? "trade").ToLowerInvariant();
        if (series is not ("trade" or "mark" or "index"))
        {
            return Results.BadRequest(new { error = "priceType must be trade, mark or index." });
        }

        if (!ApiQuery.TryParseSymbols(symbols, symbol, out var wanted, out var symbolError))
        {
            return Results.BadRequest(new { error = symbolError });
        }

        // A window is optional, but a HALF window is a typo, not a request.
        if ((from is null) != (to is null))
        {
            return Results.BadRequest(new
            {
                error = "from and to must be given together, or both omitted to take the most recent bars.",
            });
        }

        ApiQuery.Window? window = null;
        if (from is not null)
        {
            if (!ApiQuery.TryParseWindow(from, to, out var parsed, out var windowError))
            {
                return Results.BadRequest(new { error = windowError });
            }

            window = parsed;
        }

        Cursor? decoded = null;
        if (!string.IsNullOrWhiteSpace(cursor) && !Cursor.TryDecode(cursor, out decoded!))
        {
            return Results.BadRequest(new { error = "cursor is not one this API issued." });
        }

        var take = ApiQuery.ClampLimit(limit, 300, 5000);
        var warnings = new List<string>();

        await using var conn = await db.OpenAsync(ct);

        var rows = series == "trade"
            ? await TradeCandlesAsync(conn, exchange, wanted, tf, take, window, decoded, ct)
            : await PriceCandlesAsync(conn, exchange, wanted, series, tf, take, window, decoded, ct);

        var grouped = new Dictionary<string, IReadOnlyList<HistoryResponses.CandleRow>>(StringComparer.Ordinal);
        foreach (var s in wanted)
        {
            grouped[s] = [];
        }

        foreach (var group in rows.GroupBy(r => r.Symbol, StringComparer.Ordinal))
        {
            grouped[group.Key] = group.Select(g => g.Row).ToList();
        }

        foreach (var s in wanted)
        {
            if (grouped[s].Count == 0)
            {
                warnings.Add(series == "trade"
                    ? $"{s} has no {tf}m bars in range."
                    : $"{s} has no {series} bars: only Binance publishes mark and index candles, so "
                        + "this series is empty for every other venue rather than filled from the traded price.");
            }
        }

        var next = window is not null && rows.Count >= take
            ? new Cursor(rows[^1].Row.OpenTime, rows[^1].Symbol, "").Encode()
            : null;

        // The legacy half of the answer, present only for the singular `symbol` the old contract used.
        var legacySymbol = string.IsNullOrWhiteSpace(symbols) ? symbol : null;

        return TypedResults.Ok(new HistoryResponses.CandlePage(
            DateTimeOffset.UtcNow, exchange, tf, series,
            window?.From, window?.To, grouped, next, warnings,
            legacySymbol, legacySymbol is null ? null : tf,
            legacySymbol is null ? null : grouped[legacySymbol]));
    }

    private static async Task<List<(string Symbol, HistoryResponses.CandleRow Row)>> TradeCandlesAsync(
        Npgsql.NpgsqlConnection conn, string exchange, string[] symbols, int tf, int take,
        ApiQuery.Window? window, Cursor? cursor, CancellationToken ct)
    {
        // Two shapes, because "the last N bars" and "every bar in a window" are different questions:
        // the first needs a per-symbol ranking so one busy instrument cannot crowd out the others,
        // the second is a plain keyset walk.
        var sql = window is null
            ? """
              select symbol, open_time, open, high, low, close, volume, volume_quote, trade_count, bar_count, updated_at
                from (
                  select i.exchange_symbol as symbol, c.open_time, c.open, c.high, c.low, c.close,
                         c.volume, c.volume_quote, c.trade_count, c.bar_count, c.updated_at,
                         row_number() over (partition by c.exchange_instrument_id order by c.open_time desc) as rn
                    from market_candle c
                    join exchange_instrument i on i.id = c.exchange_instrument_id
                   where i.segment_code = @exchange
                     and i.exchange_symbol = any(@symbols)
                     and c.timeframe = @tf
                ) ranked
               where rn <= @take
               order by open_time, symbol
              """
            : """
              select i.exchange_symbol as symbol, c.open_time, c.open, c.high, c.low, c.close,
                     c.volume, c.volume_quote, c.trade_count, c.bar_count, c.updated_at
                from market_candle c
                join exchange_instrument i on i.id = c.exchange_instrument_id
               where i.segment_code = @exchange
                 and i.exchange_symbol = any(@symbols)
                 and c.timeframe = @tf
                 and c.open_time >= @from and c.open_time < @to
                 and (@cursorAt::timestamptz is null
                      or (c.open_time, i.exchange_symbol) > (@cursorAt, @cursorSymbol))
               order by c.open_time, i.exchange_symbol
               limit @take
              """;

        var rows = await conn.QueryAsync<(string Symbol, DateTime OpenTime, double Open, double High,
            double Low, double Close, double? Volume, double? VolumeQuote, int? TradeCount,
            short? BarCount, DateTime UpdatedAt)>(new CommandDefinition(
            sql,
            new
            {
                exchange, symbols, tf = (short)tf, take,
                from = window?.From, to = window?.To,
                cursorAt = cursor?.At, cursorSymbol = cursor?.Symbol ?? "",
            },
            cancellationToken: ct));

        return rows.Select(r => (r.Symbol, new HistoryResponses.CandleRow(
            Utc(r.OpenTime), Utc(r.OpenTime).AddMinutes(tf), r.Open, r.High, r.Low, r.Close,
            r.Volume, r.VolumeQuote, r.TradeCount, r.BarCount, Utc(r.UpdatedAt)))).ToList();
    }

    private static async Task<List<(string Symbol, HistoryResponses.CandleRow Row)>> PriceCandlesAsync(
        Npgsql.NpgsqlConnection conn, string exchange, string[] symbols, string series, int tf,
        int take, ApiQuery.Window? window, Cursor? cursor, CancellationToken ct)
    {
        // market_price_candle carries OHLC only — a mark or index series has no volume and no trade
        // count to report, and inventing zeros for them would read as a market that did not trade.
        var sql = window is null
            ? """
              select symbol, open_time, open, high, low, close
                from (
                  select i.exchange_symbol as symbol, c.open_time, c.open, c.high, c.low, c.close,
                         row_number() over (partition by c.exchange_instrument_id order by c.open_time desc) as rn
                    from market_price_candle c
                    join exchange_instrument i on i.id = c.exchange_instrument_id
                   where i.segment_code = @exchange
                     and i.exchange_symbol = any(@symbols)
                     and c.series = @series and c.timeframe = @tf
                ) ranked
               where rn <= @take
               order by open_time, symbol
              """
            : """
              select i.exchange_symbol as symbol, c.open_time, c.open, c.high, c.low, c.close
                from market_price_candle c
                join exchange_instrument i on i.id = c.exchange_instrument_id
               where i.segment_code = @exchange
                 and i.exchange_symbol = any(@symbols)
                 and c.series = @series and c.timeframe = @tf
                 and c.open_time >= @from and c.open_time < @to
                 and (@cursorAt::timestamptz is null
                      or (c.open_time, i.exchange_symbol) > (@cursorAt, @cursorSymbol))
               order by c.open_time, i.exchange_symbol
               limit @take
              """;

        var rows = await conn.QueryAsync<(string Symbol, DateTime OpenTime, decimal Open, decimal High,
            decimal Low, decimal Close)>(new CommandDefinition(
            sql,
            new
            {
                exchange, symbols, series, tf = (short)tf, take,
                from = window?.From, to = window?.To,
                cursorAt = cursor?.At, cursorSymbol = cursor?.Symbol ?? "",
            },
            cancellationToken: ct));

        return rows.Select(r => (r.Symbol, new HistoryResponses.CandleRow(
            Utc(r.OpenTime), Utc(r.OpenTime).AddMinutes(tf),
            (double)r.Open, (double)r.High, (double)r.Low, (double)r.Close,
            null, null, null, null, null))).ToList();
    }

    private static DateTimeOffset Utc(DateTime t) =>
        new(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeSpan.Zero);


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
        // Untyped rows, like the four reads above. A positional record here would put Dapper's
        // constructor matching between the query and the answer for no gain — it wants the exact
        // types it infers from the reader, and text[] does not arrive as string[].
        await using var conn = await db.OpenAsync(ct);

        var venues = await conn.QueryAsync(new CommandDefinition(
            """
            select s.code                                                       as code,
                   s.name                                                        as name,
                   s.exchange_code                                               as "exchangeCode",
                   s.kind                                                        as kind,
                   s.status                                                      as status,
                   (select min(i.first_seen_at)::date from exchange_instrument i
                     where i.segment_code = s.code)                              as since,
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = s.code and i.collect)                as instruments,
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = s.code and i.status = 'trading')     as trading,
                   (select count(*)::int from exchange_instrument i
                     where i.segment_code = s.code)                              as listed,
                   (select coalesce(array_agg(sd.dataset_code order by sd.dataset_code), '{}')
                      from segment_dataset sd
                     where sd.segment_code = s.code
                       and sd.mode <> 'disabled'
                       and sd.dataset_code not in ('discovery', 'rollup'))       as datasets
              from segment s
             where s.status = 'enabled'
             order by s.code
            """, cancellationToken: ct));

        // Summed in SQL rather than over the rows above, so the totals cannot drift from the list
        // by one of them being filtered and the other not.
        var totals = await conn.QuerySingleAsync(new CommandDefinition(
            """
            select count(*)::int                                                  as venues,
                   coalesce(sum(x.instruments), 0)::int                            as instruments,
                   coalesce(sum(x.trading), 0)::int                                as trading,
                   coalesce(sum(x.listed), 0)::int                                 as listed,
                   min(x.since)                                                    as since
              from segment s
              join lateral (
                   select (select min(i.first_seen_at)::date from exchange_instrument i
                            where i.segment_code = s.code)                          as since,
                          (select count(*)::int from exchange_instrument i
                            where i.segment_code = s.code and i.collect)            as instruments,
                          (select count(*)::int from exchange_instrument i
                            where i.segment_code = s.code and i.status = 'trading') as trading,
                          (select count(*)::int from exchange_instrument i
                            where i.segment_code = s.code)                          as listed
                   ) x on true
             where s.status = 'enabled'
            """, cancellationToken: ct));

        return Results.Ok(new { venues, totals, measuredAt = DateTimeOffset.UtcNow });
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

    private sealed record StaleRow(
        string SegmentCode,
        string Symbol,
        DateTime? ReceivedAt,
        double? AgeSeconds);
}

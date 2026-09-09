using CryptoSmithX.Database;
using Dapper;
using static CryptoSmithX.MarketData.Api.HistoryResponses;

namespace CryptoSmithX.MarketData.Api;

/// <summary>
/// The raw history surface: one endpoint per dataset, each answering a bounded window for a bounded
/// set of instruments, each paged the same way.
///
/// ONE ENDPOINT PER DATASET, deliberately. A single /history returning candles, trades, depth and
/// liquidations together would have to invent a common row shape none of them share, and its page
/// size would mean something different for every caller depending on which datasets they asked for.
/// Separate endpoints let each keep the venue's own columns and its own natural ordering.
///
/// Reads are untyped through Dapper and projected in C# rather than mapped straight into the
/// response records: this repository has been bitten three times by Dapper filling a positional
/// record by column ORDER and exact type, and the failure is silent — fields swap places and the
/// JSON still looks plausible. Value tuples with explicit types keep the mapping visible at the
/// call site.
/// </summary>
public static class HistoryEndpoints
{
    /// <summary>Rows per page. The ceiling is the same 5000 the original candles endpoint already
    /// used, so one number governs the whole surface.</summary>
    private const int DefaultLimit = 1000;
    private const int MaxLimit = 5000;

    /// <summary>The bands the depth collector measures. Not configurable here because they are not
    /// configurable in the data: 0001 fixed them, and a request for another band is a request for a
    /// measurement nobody took.</summary>
    private static readonly int[] DepthBands = [10, 25, 50];

    public sealed record ErrorBody(string Error);

    public static void MapHistoryApi(this WebApplication app)
    {
        var api = app.MapGroup("/v1");

        api.MapGet("/tickers/history", TickerHistory)
            .WithSummary("Raw ticker observations")
            .WithDescription(
                "One row per stored observation of an instrument's quote: last, bid/ask and their "
                + "sizes, mark, index and rolling 24 h turnover, exactly as recorded. Ascending by "
                + "instant then symbol.")
            .Produces<Page<TickerRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        api.MapGet("/funding", Funding)
            .WithSummary("Settled funding rates")
            .WithDescription(
                "One row per funding payment the venue has settled. nextFundingAt is null on every "
                + "row: the venue states it on the live ticker, not on settled history.")
            .Produces<Page<FundingRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        api.MapGet("/open-interest", OpenInterest)
            .WithSummary("Open interest history")
            .WithDescription(
                "Bucketed open interest as the venue publishes it. openInterestNotional is the "
                + "venue's own quote figure and is null where it publishes contracts only.")
            .Produces<Page<OpenInterestRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        api.MapGet("/depth", Depth)
            .WithSummary("Order-book depth by band")
            .WithDescription(
                "Cumulative quote notional within 10/25/50 bps of the mid, per side, with the mid "
                + "the bands were measured from. A null band was not measurable from the book that "
                + "frame carried, which is not the same as zero.")
            .Produces<Page<DepthRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        api.MapGet("/trades", Trades)
            .WithSummary("Raw trade tape")
            .WithDescription(
                "One row per trade the venue published. side is the taker side as reported; it is "
                + "not mapped to long/short, which would be an interpretation.")
            .Produces<Page<TradeRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        api.MapGet("/liquidations", Liquidations)
            .WithSummary("Per-event liquidations")
            .WithDescription(
                "Liquidations the venue marked on its own tape, with price, quantity and taker "
                + "side. Empty for venues that publish only an aggregate — see "
                + "/v1/liquidations/volume, which is a different measurement, not this one thinned.")
            .Produces<Page<LiquidationRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        api.MapGet("/liquidations/volume", LiquidationVolume)
            .WithSummary("Aggregated liquidation volume")
            .WithDescription(
                "Bucketed liquidation volume. source='analytics' is the venue's own total; "
                + "source='ws' is our sum over the venue's liquidation events, which begins when we "
                + "started listening.")
            .Produces<Page<LiquidationVolumeRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);

        api.MapGet("/order-book-snapshots", OrderBookSnapshots)
            .WithSummary("Order-book frames")
            .WithDescription(
                "Stored top-N book frames, price and quantity per level, best first. isSnapshot "
                + "distinguishes a venue snapshot from a state rebuilt from deltas.")
            .Produces<Page<BookSnapshotRow>>()
            .Produces<ErrorBody>(StatusCodes.Status400BadRequest);
    }

    private sealed record Ctx(
        string Exchange, string[] Symbols, ApiQuery.Window Window, int Limit, Cursor? Cursor);

    /// <summary>
    /// A closed window over an append-only table can be cached, because those rows never change:
    /// trade, book_topn and the snapshot history are written with `on conflict do nothing`, so a
    /// past window answers identically forever.
    ///
    /// The upserting datasets are NOT cached and must not be. A candle is rewritten when the venue
    /// corrects a late bar, and an open-interest or liquidation bucket is replaced when the venue's
    /// own total supersedes our running sum — serving sixty-second-old copies of those would hand
    /// back a figure the venue has already withdrawn.
    ///
    /// A window that runs up to now is never cached either, whatever the table: it is still filling.
    /// </summary>
    private static void Cache(HttpContext http, Ctx ctx, bool appendOnly)
    {
        var closed = ctx.Window.To <= DateTimeOffset.UtcNow;
        http.Response.Headers.CacheControl = appendOnly && closed
            ? "public, max-age=60"
            : "no-cache";
    }

    /// <summary>Validation shared by every endpoint here. Returns the error as an IResult so a
    /// handler is one guard clause away from its query.</summary>
    private static bool TryPrepare(
        string? exchange, string? symbols, string? symbol,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor,
        out Ctx ctx, out IResult error)
    {
        ctx = null!;
        error = null!;

        if (string.IsNullOrWhiteSpace(exchange))
        {
            error = Results.BadRequest(new ErrorBody("exchange is required, for example kraken-futures."));
            return false;
        }

        if (!ApiQuery.TryParseSymbols(symbols, symbol, out var parsed, out var symbolError))
        {
            error = Results.BadRequest(new ErrorBody(symbolError!));
            return false;
        }

        if (!ApiQuery.TryParseWindow(from, to, out var window, out var windowError))
        {
            error = Results.BadRequest(new ErrorBody(windowError!));
            return false;
        }

        Cursor? decoded = null;
        if (!string.IsNullOrWhiteSpace(cursor) && !Cursor.TryDecode(cursor, out decoded!))
        {
            error = Results.BadRequest(new ErrorBody(
                "cursor is not one this API issued. Pass back the nextCursor from the previous page "
                + "unchanged, or omit it to start from the beginning of the window."));
            return false;
        }

        ctx = new Ctx(exchange, parsed, window, ApiQuery.ClampLimit(limit, DefaultLimit, MaxLimit), decoded);
        return true;
    }

    /// <summary>
    /// Which of the requested symbols this venue actually lists. A typo would otherwise come back as
    /// an empty window and read as "the venue was quiet", which is the one thing an empty answer
    /// must never be confused with.
    /// </summary>
    private static async Task<HashSet<string>> KnownSymbolsAsync(
        Db db, Ctx ctx, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            """
            select exchange_symbol from exchange_instrument
             where segment_code = @exchange and exchange_symbol = any(@symbols)
            """,
            new { exchange = ctx.Exchange, symbols = ctx.Symbols },
            cancellationToken: ct));
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The envelope every page shares. A symbol is reported as empty only when the whole window fit
    /// in this one page — mid-pagination a symbol may simply not have reached its rows yet, and
    /// saying "no data" then would be wrong.
    /// </summary>
    private static Page<T> Paged<T>(
        Ctx ctx, IReadOnlyList<T> items, HashSet<string> known,
        Func<T, string> symbolOf, Func<T, Cursor> keyOf)
    {
        var next = items.Count >= ctx.Limit ? keyOf(items[^1]).Encode() : null;

        var warnings = new List<string>();
        foreach (var s in ctx.Symbols)
        {
            if (!known.Contains(s))
            {
                warnings.Add($"{s} is not an instrument on {ctx.Exchange}; no data was looked for.");
            }
        }

        if (ctx.Cursor is null && next is null)
        {
            var present = items.Select(symbolOf).ToHashSet(StringComparer.Ordinal);
            foreach (var s in ctx.Symbols)
            {
                if (known.Contains(s) && !present.Contains(s))
                {
                    warnings.Add(
                        $"{s} has no rows in this window; the venue was asked and this is its answer, "
                        + "not a gap in collection. See /v1/coverage for what is held.");
                }
            }
        }

        return new Page<T>(
            DateTimeOffset.UtcNow, ctx.Exchange,
            new Query(ctx.Exchange, ctx.Symbols, ctx.Window.From, ctx.Window.To, ctx.Limit),
            items, next, warnings);
    }

    /// <summary>Npgsql hands timestamptz back as DateTime with Kind=Utc; the API states UTC
    /// explicitly rather than letting a serializer guess an offset.</summary>
    private static DateTimeOffset Utc(DateTime t) =>
        new(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>The keyset predicate and ordering shared by the flat endpoints. Written once because
    /// a page that sorts differently from the way it resumes silently drops rows.</summary>
    private const string KeysetTail =
        """
           and (@cursorAt::timestamptz is null
                or (t.{0}, i.exchange_symbol, {1}) > (@cursorAt, @cursorSymbol, @cursorTie))
         order by t.{0}, i.exchange_symbol, {1}
         limit @limit
        """;

    private static string Keyset(string timeColumn, string tiebreak) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, KeysetTail, timeColumn, tiebreak);

    private static object CursorArgs(Ctx ctx) => new
    {
        exchange = ctx.Exchange,
        symbols = ctx.Symbols,
        from = ctx.Window.From,
        to = ctx.Window.To,
        limit = ctx.Limit,
        cursorAt = ctx.Cursor?.At,
        cursorSymbol = ctx.Cursor?.Symbol ?? "",
        cursorTie = ctx.Cursor?.Tiebreak ?? "",
    };

    private static async Task<IResult> TickerHistory(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        Cache(http, ctx, appendOnly: true);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, double Last, double Bid,
                double Ask, double BidSize, double AskSize, double Mark, double Index, double Turnover)>(
            new CommandDefinition(
                """
                select t.received_at, i.exchange_symbol, t.last_price, t.bid_price, t.ask_price,
                       t.bid_size, t.ask_size, t.mark_price, t.index_price, t.turnover_24h
                  from market_snapshot t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.received_at >= @from and t.received_at < @to
                """ + Keyset("received_at", "''"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r => new TickerRow(
            Utc(r.Utc), r.Symbol, r.Last, r.Bid, r.Ask, r.BidSize, r.AskSize, r.Mark, r.Index, r.Turnover));

        return TypedResults.Ok(Paged(ctx, items, known, x => x.Symbol, x => new Cursor(x.Utc, x.Symbol, "")));
    }

    private static async Task<IResult> Funding(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        Cache(http, ctx, appendOnly: true);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, double Rate, short? Interval)>(
            new CommandDefinition(
                """
                select t.funding_time, i.exchange_symbol, t.rate, t.funding_interval_hours
                  from funding_rate_history t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.funding_time >= @from and t.funding_time < @to
                """ + Keyset("funding_time", "''"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r => new FundingRow(Utc(r.Utc), r.Symbol, r.Rate, r.Interval, null));

        return TypedResults.Ok(Paged(ctx, items, known, x => x.Symbol, x => new Cursor(x.Utc, x.Symbol, "")));
    }

    private static async Task<IResult> OpenInterest(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        Cache(http, ctx, appendOnly: false);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, int Interval, decimal Close, decimal? Quote)>(
            new CommandDefinition(
                """
                select t.bucket_time, i.exchange_symbol, t.interval_s, t.oi_close, t.oi_quote
                  from open_interest_history t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.bucket_time >= @from and t.bucket_time < @to
                """ + Keyset("bucket_time", "t.interval_s::text"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r => new OpenInterestRow(
            Utc(r.Utc), r.Symbol, r.Interval, r.Close, r.Quote));

        if (items.Count > 0 && items.TrueForAll(i => i.OpenInterestNotional is null))
        {
            // Said out loud rather than left as a column of nulls: this venue publishes contracts
            // only, and multiplying by a mark price from a different row would answer a question
            // nobody asked with a number the venue never stated.
            var page = Paged(ctx, items, known, x => x.Symbol,
                x => new Cursor(x.Utc, x.Symbol, x.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return TypedResults.Ok(page with
            {
                Warnings = [.. page.Warnings,
                    $"{ctx.Exchange} publishes open interest in contracts only; openInterestNotional "
                    + "is null on every row rather than derived from a mark price."],
            });
        }

        return TypedResults.Ok(Paged(ctx, items, known, x => x.Symbol,
            x => new Cursor(x.Utc, x.Symbol, x.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static async Task<IResult> Depth(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol, string? bandsBps,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        var bands = DepthBands.ToList();
        if (!string.IsNullOrWhiteSpace(bandsBps))
        {
            bands = [];
            foreach (var part in bandsBps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var b)
                    || !DepthBands.Contains(b))
                {
                    return Results.BadRequest(new ErrorBody(
                        $"bandsBps={part} is not measured. The stored bands are "
                        + $"{string.Join(", ", DepthBands)} — a band nobody measured cannot be served."));
                }

                if (!bands.Contains(b))
                {
                    bands.Add(b);
                }
            }
        }

        Cache(http, ctx, appendOnly: true);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, double? B10, double? A10,
                double? B25, double? A25, double? B50, double? A50, double? Ref)>(
            new CommandDefinition(
                """
                select t.received_at, i.exchange_symbol,
                       t.depth_bid_10bps, t.depth_ask_10bps,
                       t.depth_bid_25bps, t.depth_ask_25bps,
                       t.depth_bid_50bps, t.depth_ask_50bps,
                       t.depth_ref
                  from market_snapshot t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.received_at >= @from and t.received_at < @to
                   and t.depth_at is not null
                """ + Keyset("received_at", "''"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r =>
        {
            var bid = new Dictionary<string, double?>();
            var ask = new Dictionary<string, double?>();
            foreach (var b in bands)
            {
                bid[b.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    b == 10 ? r.B10 : b == 25 ? r.B25 : r.B50;
                ask[b.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    b == 10 ? r.A10 : b == 25 ? r.A25 : r.A50;
            }

            return new DepthRow(Utc(r.Utc), r.Symbol, bid, ask, r.Ref);
        });

        return TypedResults.Ok(Paged(ctx, items, known, x => x.Symbol, x => new Cursor(x.Utc, x.Symbol, "")));
    }

    private static async Task<IResult> Trades(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        Cache(http, ctx, appendOnly: true);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, string Uid, decimal Price,
                decimal Qty, string? Side, string? Type, decimal Multiplier)>(
            new CommandDefinition(
                """
                select t.event_time, i.exchange_symbol, t.venue_uid, t.price, t.qty,
                       t.taker_side, t.trade_type, i.contract_multiplier
                  from trade t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.event_time >= @from and t.event_time < @to
                """ + Keyset("event_time", "t.venue_uid"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r => new TradeRow(
            Utc(r.Utc), r.Symbol, r.Uid, r.Side ?? "unknown", r.Price, r.Qty,
            // The schema's own unit rule (0032): qty is in the venue's units, and the instrument's
            // multiplier is what turns it into base, so notional is price x qty x multiplier.
            r.Price * r.Qty * r.Multiplier, r.Type));

        return TypedResults.Ok(Paged(ctx, items, known, x => x.Symbol,
            x => new Cursor(x.Utc, x.Symbol, x.TradeId ?? "")));
    }

    private static async Task<IResult> Liquidations(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        Cache(http, ctx, appendOnly: true);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, string Uid, decimal Price,
                decimal Qty, string? Side, string? Type, decimal Multiplier)>(
            new CommandDefinition(
                """
                select t.event_time, i.exchange_symbol, t.venue_uid, t.price, t.qty,
                       t.taker_side, t.trade_type, i.contract_multiplier
                  from trade t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.event_time >= @from and t.event_time < @to
                   and t.trade_type in ('liquidation', 'partial_liquidation', 'termination')
                """ + Keyset("event_time", "t.venue_uid"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r => new LiquidationRow(
            Utc(r.Utc), r.Symbol, r.Side ?? "unknown", r.Price, r.Qty,
            r.Price * r.Qty * r.Multiplier, r.Type));

        var page = Paged(ctx, items, known, x => x.Symbol, x => new Cursor(x.Utc, x.Symbol, ""));
        if (items.Count == 0)
        {
            page = page with
            {
                Warnings = [.. page.Warnings,
                    "No per-event liquidations in this window. Only venues that mark liquidations on "
                    + "their own tape produce rows here; where a venue publishes an aggregate "
                    + "instead, it is served by /v1/liquidations/volume."],
            };
        }

        return TypedResults.Ok(page);
    }

    private static async Task<IResult> LiquidationVolume(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        Cache(http, ctx, appendOnly: false);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, int Interval, decimal Volume,
                string Unit, string? Source)>(
            new CommandDefinition(
                """
                select t.bucket_time, i.exchange_symbol, t.interval_s, t.volume, t.volume_unit, t.source
                  from liquidation_volume_history t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.bucket_time >= @from and t.bucket_time < @to
                """ + Keyset("bucket_time", "t.interval_s::text"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r => new LiquidationVolumeRow(
            Utc(r.Utc), r.Symbol, r.Interval, r.Volume, r.Unit, r.Source));

        return TypedResults.Ok(Paged(ctx, items, known, x => x.Symbol,
            x => new Cursor(x.Utc, x.Symbol, x.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static async Task<IResult> OrderBookSnapshots(
        HttpContext http, Db db, string? exchange, string? symbols, string? symbol, int? depth,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor, CancellationToken ct)
    {
        if (!TryPrepare(exchange, symbols, symbol, from, to, limit, cursor, out var ctx, out var error))
        {
            return error;
        }

        // Book frames are far heavier per row than a quote: a page of 5000 twenty-five-level frames
        // is a quarter of a million levels. Its own, lower ceiling.
        var frameLimit = ApiQuery.ClampLimit(limit, 200, 1000);
        ctx = ctx with { Limit = frameLimit };
        var wanted = depth is null ? int.MaxValue : Math.Max(1, depth.Value);

        Cache(http, ctx, appendOnly: true);
        var known = await KnownSymbolsAsync(db, ctx, ct);
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(DateTime Utc, string Symbol, long Seq, bool IsSnapshot,
                short Levels, decimal[] BidPx, decimal[] BidQty, decimal[] AskPx, decimal[] AskQty,
                int[]? BidN, int[]? AskN)>(
            new CommandDefinition(
                """
                select t.observed_at, i.exchange_symbol, t.seq, t.is_snapshot, t.levels,
                       t.bid_px, t.bid_qty, t.ask_px, t.ask_qty, t.bid_n, t.ask_n
                  from book_topn t
                  join exchange_instrument i on i.id = t.exchange_instrument_id
                 where i.segment_code = @exchange
                   and i.exchange_symbol = any(@symbols)
                   and t.observed_at >= @from and t.observed_at < @to
                """ + Keyset("observed_at", "t.seq::text"),
                CursorArgs(ctx), cancellationToken: ct))).ToList();

        var items = rows.ConvertAll(r => new BookSnapshotRow(
            Utc(r.Utc), r.Symbol, r.Seq, r.IsSnapshot, r.Levels,
            Levels(r.BidPx, r.BidQty, r.BidN, wanted),
            Levels(r.AskPx, r.AskQty, r.AskN, wanted)));

        return TypedResults.Ok(Paged(ctx, items, known, x => x.Symbol,
            x => new Cursor(x.Utc, x.Symbol, x.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <summary>Parallel arrays as stored, zipped into levels and truncated to what was asked for.
    /// Order is the venue's own — best first — and is not re-sorted here.</summary>
    private static List<BookLevel> Levels(
        decimal[] prices, decimal[] quantities, int[]? orders, int take)
    {
        var count = Math.Min(Math.Min(prices.Length, quantities.Length), take);
        var levels = new List<BookLevel>(count);
        for (var i = 0; i < count; i++)
        {
            levels.Add(new BookLevel(prices[i], quantities[i], orders is not null && i < orders.Length ? orders[i] : null));
        }

        return levels;
    }
}

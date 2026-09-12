using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Bitget;

/// <summary>
/// Bitget's USDT-settled perpetuals as an <see cref="IExchangeMarketData"/>.
///
/// <b>Why this venue is cheap.</b> One <c>mix/market/tickers</c> closes the snapshot: last, both
/// sides with their sizes, mark, index, funding rate, open interest, 24-hour volume in both units —
/// and the venue's own <c>ts</c> inside each row, so the figure carries the venue's clock rather
/// than ours. Measured live: 787 rows, 396 KB, 0.40 s, zero empty fields.
///
/// <b>What it costs to wait, stated once.</b> There is no open-interest history on this venue at
/// all — <c>mix/market/open-interest</c> answers with one current number and no series. Candles
/// reach back to 2021 and funding about ninety days, so OI is the single column whose past is lost
/// by not collecting today, and it is lost for every day we are not running. That is the argument
/// for enabling it now rather than later, and it is the only one this venue needs.
///
/// <b>Depth and the tape are REST, per symbol, on the depth sweep's own cadence.</b> Measured:
/// forty parallel <c>merge-depth</c> calls returned 40×200 in 1.11 s with no refusal, and this
/// adapter issues one per collected symbol. The socket would be cheaper — this venue's book rule
/// (<c>update.pseq == prev.seq</c> with a snapshot on the socket) is word for word
/// <see cref="Weex.WeexBookBuilder"/>'s — but nothing here waits for it.
/// </summary>
public sealed class BitgetPerpMarketData : IExchangeMarketData
{
    private readonly BitgetClient _client;
    private readonly RestTape _tape = new();

    public BitgetPerpMarketData(BitgetClient client) => _client = client;

    public string SegmentCode => "bitget-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("candles_mark", "rest"),
        new("candles_index", "rest"),
        // open_interest is ABSENT on purpose and it is the one real gap here: the venue publishes a
        // current number and no history at all, so there is nothing for the history collector to
        // fetch. The current value still reaches the snapshot every pass through the ticker.
        // liquidations likewise — no public route, and the tape carries no flag to derive one from.
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var contracts = await _client.GetContractsAsync(ct);

        var list = new List<Instrument>(contracts.Count);
        foreach (var c in contracts)
        {
            // Perpetuals only — the venue's own word for it, not a reading of the symbol.
            if (!string.Equals(c.SymbolType, "perpetual", StringComparison.Ordinal))
            {
                continue;
            }

            if (c.BaseCoin is not { Length: > 0 } || c.QuoteCoin is not { Length: > 0 })
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: c.Symbol,
                BaseAssetRaw: c.BaseCoin,
                QuoteAssetRaw: c.QuoteCoin,
                // Base units already: holdingAmount on BTCUSDT reads 35 194 against a 77k price, and
                // sizeMultiplier equals minTradeNum on every contract checked — that is an order
                // STEP, not a contract size, so there is nothing to scale.
                ContractMultiplier: 1m,
                PriceStep: TickSize(c.PriceEndStep, c.PricePlace),
                // The increment the venue accepts. Equal to minTradeNum on every contract sampled,
                // which is what says both are a step rather than one being a contract size.
                QtyStep: Dec(c.SizeMultiplier),
                MinQty: Dec(c.MinTradeNum),
                MinNotional: Dec(c.MinTradeUsdt),
                FundingIntervalHours: short.TryParse(
                    c.FundInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h > 0
                    ? h
                    : null,
                ListedAt: Instant(c.LaunchTime),
                Status: string.Equals(c.SymbolStatus, "normal", StringComparison.Ordinal)
                    ? InstrumentStatus.Trading
                    // 'maintain', 'limit_open', 'restrictedAPI', 'off' — all of them mean listed but
                    // not freely trading, which is what Halted says. None of them is a delisting,
                    // and inventing one from a pause is the mistake the WEEX adapter records.
                    : InstrumentStatus.Halted,
                RawJson: JsonSerializer.Serialize(c)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var rows = await _client.GetTickersAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(rows.Count);
        foreach (var t in rows)
        {
            list.Add(new Ticker(
                ExchangeSymbol: t.Symbol,
                ReceivedAt: now,
                // The instant of the newest print this venue's tape has handed us, which is what
                // the Last-trade column holds. It comes from the tape rather than the ticker
                // because none of these five publish a "time of last trade" on the ticker at all,
                // and that column was empty for every venue but Avantis while five tapes sat in
                // the same process already knowing the answer. As fresh as the tape's own pass,
                // never fresher, and null until that pass has run.
                LastTradeAt: _tape.Last(t.Symbol)?.At,
                LastPrice: Num(t.LastPr),
                BidPrice: Num(t.BidPr),
                AskPrice: Num(t.AskPr),
                BidSize: Num(t.BidSz),
                AskSize: Num(t.AskSz),
                MarkPrice: Num(t.MarkPrice),
                IndexPrice: Num(t.IndexPrice),
                FundingRate: Num(t.FundingRate),
                Turnover24h: Num(t.QuoteVolume),
                OpenInterest: Num(t.HoldingAmount),
                OpenInterestAt: now,
                Depth: null,
                // THE VENUE'S OWN CLOCK, from inside the frame. Without it the age under a figure
                // would be the age of our write rather than of the observation.
                VenueTs: Instant(t.Ts),
                Volume24hBase: Num(t.BaseVolume)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetCandles1mAsync(exchangeSymbol, from, to, ct);

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            // [ms, open, high, low, close, baseVol, quoteVol] — oldest first on this venue, which
            // is why nothing here depends on the order.
            if (c.Length < 7 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Req(c[1]),
                High: Req(c[2]),
                Low: Req(c[3]),
                Close: Req(c[4]),
                Volume: Req(c[5]),
                TradeCount: null,
                VolumeQuote: Num(c[6])));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetFundingHistoryAsync(exchangeSymbol, ct);

        var list = new List<FundingRate>(rows.Count);
        foreach (var f in rows)
        {
            if (Num(f.FundingRate) is not { } rate || Instant(f.FundingTime) is not { } at)
            {
                continue;
            }

            // The route takes no window, so the window is applied here rather than the caller
            // receiving rows it did not ask for.
            if (at < from || at > to)
            {
                continue;
            }

            list.Add(new FundingRate(exchangeSymbol, at, rate));
        }

        return list;
    }

    /// <summary>The book, and the tape with it — see the Bybit adapter's own remarks on why the two
    /// share a call site, and why a tape failure never costs the book.</summary>
    /// <summary>
    /// The book, and the tape with it.
    ///
    /// <b>Why this may ask twice.</b> <c>limit=max</c> is a hundred levels and no more, and a
    /// hundred levels of BTCUSDT at native precision reach 3.7 bps from the mid — measured — so all
    /// three depth bands were unbounded and all three columns empty on this venue's most traded
    /// contract. The route's <c>precision</c> parameter buys span by merging the ladder onto a
    /// coarser price grid, one decimal per step, and that step is RELATIVE: scale1 is ten ticks
    /// whatever the price.
    ///
    /// Relative is not the same as harmless. Ten ticks is 0.14 bps on BTCUSDT and 11.8 bps on
    /// DOGEUSDT, measured on the same afternoon — coarser there than the narrowest band we report,
    /// which would leave that band's edges meaning nothing. So the coarser ladder is asked for only
    /// when the native one falls short, and kept only when its own grid is still fine against the
    /// bands: see <see cref="Wider"/>.
    /// </summary>
    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var book = await _client.GetDepthAsync(exchangeSymbol, NativePrecision, ct);

        if (Wider(book) is { } merged)
        {
            book = await _client.GetDepthAsync(exchangeSymbol, MergedPrecision, ct);

            if (!Usable(book))
            {
                // The coarser grid would cost more than the span is worth. Back to the native
                // ladder, whose bands are narrower but mean what they say.
                book = merged;
            }
        }

        try
        {
            Fold(exchangeSymbol, await _client.GetFillsAsync(exchangeSymbol, ct));
        }
        catch (HttpRequestException)
        {
        }

        // Base units on this venue, so the multiplier is one.
        return ContractBook.Compute(
            Levels(book.Bids), Levels(book.Asks), contractMultiplier: 1,
            at: Instant(book.Ts) ?? DateTimeOffset.UtcNow);
    }

    /// <summary>The venue's own tick — the finest ladder it serves.</summary>
    private const string NativePrecision = "scale0";

    /// <summary>Ten ticks a level. One step, not two: scale2 is a hundred ticks, which is 1.4 bps on
    /// BTCUSDT and past the point where a 10 bps band still has edges.</summary>
    private const string MergedPrecision = "scale1";

    /// <summary>The widest band this adapter reports, and so the span a ladder has to cover for all
    /// three to be sums rather than undercounts.</summary>
    private const double WidestBandBps = 50;

    /// <summary>
    /// The coarsest price grid that still leaves the narrowest band meaningful — a tenth of 10 bps.
    /// A level wider than this and the 10 bps band's edge is decided by where a bucket happened to
    /// fall rather than by where the orders are.
    /// </summary>
    private const double FinestBandBps = 1;

    /// <summary>The native ladder, when it falls short of <see cref="WidestBandBps"/> and a wider
    /// one is worth asking for; null when it already spans the bands.</summary>
    private static BitgetDepth? Wider(BitgetDepth book) =>
        Span(book) is { } span && span < WidestBandBps ? book : null;

    /// <summary>Whether a merged ladder's own grid is still fine enough to put an edge on the
    /// narrowest band.</summary>
    private static bool Usable(BitgetDepth book)
    {
        var bids = Levels(book.Bids);
        if (bids.Count < 2 || Mid(book) is not { } mid || mid <= 0)
        {
            return false;
        }

        var lowest = double.MaxValue;
        var highest = double.MinValue;
        foreach (var (price, _) in bids)
        {
            lowest = Math.Min(lowest, price);
            highest = Math.Max(highest, price);
        }

        var step = (highest - lowest) / (bids.Count - 1) / mid * 10_000.0;
        return step < FinestBandBps;
    }

    /// <summary>How far the thinner side of this ladder reaches from the mid, in bps.</summary>
    private static double? Span(BitgetDepth book)
    {
        var bids = Levels(book.Bids);
        var asks = Levels(book.Asks);
        if (bids.Count == 0 || asks.Count == 0 || Mid(book) is not { } mid || mid <= 0)
        {
            return null;
        }

        var bidReach = (mid - bids.Min(l => l.Price)) / mid * 10_000.0;
        var askReach = (asks.Max(l => l.Price) - mid) / mid * 10_000.0;
        return Math.Min(bidReach, askReach);
    }

    private static double? Mid(BitgetDepth book)
    {
        var bids = Levels(book.Bids);
        var asks = Levels(book.Asks);
        return bids.Count == 0 || asks.Count == 0
            ? null
            : (bids.Max(l => l.Price) + asks.Min(l => l.Price)) / 2;
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    /// <summary>Mark or index bars — the same array shape the traded candles use, minus any meaning
    /// for the volume columns, which is why only the four prices are read.</summary>
    public async Task<IReadOnlyList<PriceCandle>> GetPriceCandles1mAsync(
        string exchangeSymbol, string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (series is not ("mark" or "index"))
        {
            return [];
        }

        var rows = await _client.GetPriceCandles1mAsync(exchangeSymbol, series, from, to, ct);

        var list = new List<PriceCandle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.Length < 5 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new PriceCandle(
                exchangeSymbol, series, openTime, Req(c[1]), Req(c[2]), Req(c[3]), Req(c[4])));
        }

        return list;
    }

    private void Fold(string exchangeSymbol, IReadOnlyList<BitgetFill> fills)
    {
        var events = new List<TradeEvent>(fills.Count);
        foreach (var f in fills)
        {
            if (f.TradeId is not { Length: > 0 } id
                || Num(f.Price) is not { } price || price <= 0
                || Num(f.Size) is not { } size
                || Instant(f.Ts) is not { } at)
            {
                continue;
            }

            events.Add(new TradeEvent(
                exchangeSymbol, at, id, null, price, size,
                string.Equals(f.Side, "buy", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell",
                null));

            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    /// <summary>[price, size] pairs of JSON numbers into levels.</summary>
    private static List<(double Price, double Qty)> Levels(IReadOnlyList<double[]>? raw)
    {
        var levels = new List<(double Price, double Qty)>(raw?.Count ?? 0);
        foreach (var l in raw ?? [])
        {
            if (l.Length >= 2 && double.IsFinite(l[0]) && l[0] > 0 && double.IsFinite(l[1]) && l[1] > 0)
            {
                levels.Add((l[0], l[1]));
            }
        }

        return levels;
    }

    /// <summary>
    /// The venue's composite tick: <c>priceEndStep</c> scaled by <c>pricePlace</c> decimals, because
    /// Bitget publishes the step and its exponent as two fields rather than one number.
    ///
    /// Verified against the venue's own live prices on five contracts spanning ten orders of
    /// magnitude: BTC 0.1 against 77 153.6, ETH 0.01 against 2 524.63, DOGE 1e-5 against 0.08486,
    /// PEPE 1e-10 against 0.000003395, SOL 0.001 against 101.981. A step that did not divide the
    /// price it belongs to would have shown up on the first of those.
    /// </summary>
    private static decimal? TickSize(string? endStep, string? pricePlace)
    {
        if (Dec(endStep) is not { } step
            || !int.TryParse(pricePlace, NumberStyles.Integer, CultureInfo.InvariantCulture, out var places)
            || places < 0 || places > 18)
        {
            return null;
        }

        // decimal, not double: a tick is a grid the venue enforces exactly, and 1e-10 through a
        // binary float stops being the number the venue named.
        for (var i = 0; i < places; i++)
        {
            step /= 10m;
        }

        return step > 0 ? step : null;
    }

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"Bitget sent a bar with an unreadable figure: '{s}'");

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTimeOffset? Instant(string? ms) =>
        long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(v)
            : null;
}

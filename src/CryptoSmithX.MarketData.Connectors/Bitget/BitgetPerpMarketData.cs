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
/// <b>Depth is not declared yet.</b> Bid and ask size come off the ticker, so those columns fill;
/// the cumulative bands need a maintained book, and this venue's book rule
/// (<c>update.pseq == prev.seq</c> with a snapshot on the socket) is word for word
/// <see cref="Weex.WeexBookBuilder"/>'s — already written, already debugged, and the next slice.
/// </summary>
public sealed class BitgetPerpMarketData : IExchangeMarketData
{
    private readonly BitgetClient _client;

    public BitgetPerpMarketData(BitgetClient client) => _client = client;

    public string SegmentCode => "bitget-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        // open_interest is ABSENT on purpose and it is the one real gap here: the venue publishes a
        // current number and no history at all, so there is nothing for the history collector to
        // fetch. The current value still reaches the snapshot every pass through the ticker.
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

    /// <summary>Null forever until the socket is wired — the cumulative bands need a maintained
    /// book, and this venue's REST depth is one call per symbol.</summary>
    public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct) =>
        Task.FromResult<Depth?>(null);

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

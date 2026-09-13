using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// Binance's spot market as an <see cref="IExchangeMarketData"/>.
///
/// <b>Spot is a perpetual minus seven figures, and they are absent rather than uncollected.</b>
/// There is no mark price, no index price, no funding rate, no predicted funding, no next funding
/// instant, no open interest and no liquidation feed — not because this adapter skipped them but
/// because a spot market has none of those things. They go to the wire as null, and the page is owed
/// a way to say "this market has no such number" rather than "the collector is quiet"; that is
/// <c>segment.kind</c>'s job and it is not done here.
///
/// <b>One bulk call closes the snapshot.</b> <c>ticker/24hr</c> carries the rolling-window figures
/// and the live top of book together, so <c>ticker/bookTicker</c> is never called — see
/// <see cref="BinanceSpotTicker"/> for the measurement behind that.
///
/// <b>Quantities are base-asset units</b>, like USDⓈ-M and unlike the contract venues, so the
/// multiplier is one and the depth bands are the same arithmetic every other book-backed venue uses.
///
/// <b>A different host is a different budget.</b> api.binance.com allows 6 000 weight a minute where
/// fapi.binance.com allows 2 400, measured; the gate is keyed on the host (0054) so this surface
/// neither spends nor is throttled by the futures budget.
/// </summary>
public sealed class BinanceSpotMarketData : IExchangeMarketData
{
    /// <summary>
    /// How deep the book is asked for, and what it costs.
    ///
    /// Measured on BTCUSDT 2026-09-13: 100 levels reach 2.0 bps from the mid, 500 reach 6.9, 1 000
    /// reach 14.6 and 5 000 reach 86.7. Only the last bounds all three bands, and a band that is not
    /// bounded is an undercount the column has to leave empty.
    ///
    /// USDⓈ-M answers the same question with 100 and says so plainly: at 570 symbols the deep bands
    /// are not worth four times the weight, and its WebSocket book fills them instead. This surface
    /// has no socket, so REST is the only answer there will be — and the pairs whose bands would
    /// stay empty are BTC and ETH, the first rows anyone reads.
    ///
    /// <b>The arithmetic, so that widening the collected set is a decision with a number on it.</b>
    /// This limit costs weight 249 a symbol, measured off <c>x-mbx-used-weight-1m</c>, against a
    /// budget of 6 000 a ROLLING MINUTE. The bound is on the SWEEP, not on the cadence: a pass
    /// issues its requests as fast as the gate lets them go, so N symbols put 250N weight into the
    /// same few seconds however far apart the passes are. About twenty-four symbols is therefore the
    /// ceiling, and stretching the interval does not raise it.
    ///
    /// Written down after getting it wrong: the first version of this comment divided the weight by
    /// the interval and concluded a hundred symbols would fit. Fifty-two collected symbols answered
    /// 429 on the first pass. An average over a window is not a ceiling on a burst inside it.
    ///
    /// <b>What this really exposes is that the gate counts REQUESTS and this venue counts WEIGHT.</b>
    /// One gate ceiling cannot express "twenty of these or twelve hundred of those", so the number
    /// that keeps this surface inside its budget is the size of the collected set, chosen by an
    /// operator per instrument, and not a rate anyone can set. The venue lists 765 USD-family pairs;
    /// collecting all of them at this depth is not a thing that fits.
    /// </summary>
    private const int DepthLimit = 5000;

    /// <summary>Rows per klines call. The venue allows 1 000 on spot and charges by the limit asked
    /// for, in the same table futures uses.</summary>
    private const int KlineLimit = 1000;

    /// <summary>Prints per tape poll.</summary>
    private const int TradeLimit = 1000;

    private readonly BinanceSpotClient _client;
    private readonly RestTape _tape = new();

    public BinanceSpotMarketData(BinanceSpotClient client) => _client = client;

    public string SegmentCode => "binance-spot";

    /// <summary>
    /// Exactly what a spot market has.
    ///
    /// Not declared, and each for its own reason rather than for want of work: <c>funding</c>,
    /// <c>open_interest</c>, <c>liquidations</c>, <c>candles_mark</c> and <c>candles_index</c>
    /// describe measurements a spot market does not make; <c>book</c> would promise a maintained
    /// socket book, and there is no socket here at all — the precedent for stating that in words is
    /// in <see cref="BybitPerpMarketData"/>; <c>vault_*</c> belongs to a vault-backed venue.
    /// </summary>
    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var symbols = await _client.GetSymbolsAsync(ct);

        var list = new List<Instrument>(symbols.Count);
        foreach (var s in symbols)
        {
            // The venue's own statement that this pair is not tradable on spot. Forty of the 1 086
            // USD-family rows say so, every one of them also BREAK — but the flag is what says it,
            // and a pair that cannot be traded on spot does not belong in a spot segment whatever
            // its status reads.
            if (s.IsSpotTradingAllowed == false)
            {
                continue;
            }

            if (s.BaseAsset is not { Length: > 0 } || s.QuoteAsset is not { Length: > 0 })
            {
                continue;
            }

            var price = Filter(s, "PRICE_FILTER");
            var lot = Filter(s, "LOT_SIZE");

            list.Add(new Instrument(
                ExchangeSymbol: s.Symbol,
                // The venue's own spelling, unnormalised, exactly as USDⓈ-M leaves it: the alias
                // table is the one opinion about asset identity in this tree, and a second one here
                // would be a second answer to the same question.
                BaseAssetRaw: s.BaseAsset,
                QuoteAssetRaw: s.QuoteAsset,
                // Base-asset units. Nothing on spot is a contract.
                ContractMultiplier: 1m,
                PriceStep: Required(price?.TickSize, s, "PRICE_FILTER.tickSize"),
                QtyStep: Required(lot?.StepSize, s, "LOT_SIZE.stepSize"),
                MinQty: Required(lot?.MinQty, s, "LOT_SIZE.minQty"),
                // Present on every USD-family row when this was captured, and still read as
                // optional: a missing NOTIONAL means the venue defines none, not zero.
                MinNotional: Decimal(Filter(s, "NOTIONAL")?.MinNotional),
                // A spot pair has no funding, so there is no interval to carry.
                FundingIntervalHours: null,
                // exchangeInfo carries no onboard date on this surface — futures has one, spot does
                // not. Null is "the venue does not say", and first_seen_at records when we did.
                ListedAt: null,
                Status: Status(s),
                RawJson: s.RawJson));
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
                // The tape's newest print, for the Last-trade column. This route carries the last
                // price but never the instant it traded at.
                LastTradeAt: _tape.Last(t.Symbol)?.At,
                LastPrice: Parse(t.LastPrice),
                BidPrice: Parse(t.BidPrice),
                AskPrice: Parse(t.AskPrice),
                BidSize: Parse(t.BidQty),
                AskSize: Parse(t.AskQty),
                // ABSENT, all seven of them, and absent is what a spot market is: there is no mark,
                // no index, no funding and no open interest to report. Nulls here are the market's
                // shape and not a gap in collection.
                MarkPrice: null,
                IndexPrice: null,
                FundingRate: null,
                Turnover24h: Parse(t.QuoteVolume),
                OpenInterest: null,
                OpenInterestAt: null,
                Depth: null,
                VenueTs: t.CloseTime is { } ms && ms > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : null,
                Volume24hBase: Parse(t.Volume)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetCandles1mAsync(exchangeSymbol, from, to, KlineLimit, ct);

        var list = new List<Candle>(rows.Count);
        foreach (var r in rows)
        {
            if (r.Length < 9)
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(r[0].GetInt64());

            // Closed bars only: the one covering `to` is still forming, and the venue returns it as
            // an ordinary row with partial volume — exactly the value that would be wrong forever
            // once stored.
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Parse(r[1].GetString())!.Value,
                High: Parse(r[2].GetString())!.Value,
                Low: Parse(r[3].GetString())!.Value,
                Close: Parse(r[4].GetString())!.Value,
                // Index 5 is base-asset volume; index 7 is the quote-asset volume, measured
                // independently of it; index 8 is the trade count.
                Volume: Parse(r[5].GetString()) ?? 0,
                TradeCount: r[8].GetInt32(),
                VolumeQuote: Parse(r[7].GetString())));
        }

        return list;
    }

    /// <summary>A spot market pays no funding, so there is no series to return and none is
    /// claimed in <see cref="Capabilities"/>.</summary>
    public Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FundingRate>>([]);

    /// <summary>The book, and the tape with it — one call site for the same reason the contract
    /// venues have one: the tape is polled, and folding it here is a poll that costs no extra
    /// sweep.</summary>
    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var book = await _client.GetDepthAsync(exchangeSymbol, DepthLimit, ct);

        try
        {
            Fold(exchangeSymbol, await _client.GetAggTradesAsync(exchangeSymbol, TradeLimit, ct));
        }
        catch (HttpRequestException)
        {
            // Including a 429: the book reading still stands, and the gate has already been told to
            // slow down by the exception itself.
        }

        var bids = Levels(book.Bids);
        var asks = Levels(book.Asks);

        // OUR clock, and it has to be. This route carries no timestamp at all — not in the body, not
        // in a header — so depth_at records when we read it rather than when the venue took it. That
        // is a weaker statement than the futures side makes, and saying so is the point of the
        // column being separate.
        return DepthMath.Compute(bids, asks, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    private void Fold(string exchangeSymbol, IReadOnlyList<BinanceAggTrade> trades)
    {
        var events = new List<TradeEvent>(trades.Count);
        foreach (var t in trades)
        {
            if (t.Id is not { } id
                || Parse(t.Price) is not { } price
                || Parse(t.Qty) is not { } qty
                || t.Timestamp is not { } ms || ms <= 0)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeMilliseconds(ms);

            events.Add(new TradeEvent(
                exchangeSymbol,
                at,
                id.ToString(CultureInfo.InvariantCulture),
                id,
                price,
                qty,
                // 'm' is TRUE when the BUYER was the maker, which means the taker SOLD. The field
                // names the resting side, not the aggressing one, and read the other way round every
                // print on the venue changes sides.
                t.BuyerIsMaker == true ? "sell" : "buy",
                null));

            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    private static List<(double Price, double Qty)> Levels(IReadOnlyList<string[]>? raw)
    {
        var levels = new List<(double Price, double Qty)>(raw?.Count ?? 0);
        foreach (var l in raw ?? [])
        {
            if (l.Length >= 2 && Parse(l[0]) is { } px && px > 0 && Parse(l[1]) is { } qty && qty > 0)
            {
                levels.Add((px, qty));
            }
        }

        return levels;
    }

    private static BinanceSpotFilter? Filter(BinanceSpotSymbol s, string type) =>
        s.Filters?.FirstOrDefault(f => string.Equals(f.FilterType, type, StringComparison.Ordinal));

    /// <summary>
    /// The venue's listing state, and a THROW on anything unrecognised — the same discipline
    /// USDⓈ-M applies, for the same reason and on the same venue.
    ///
    /// The tempting fallback, leaving an unknown status out of this pass, is not a quiet no-op:
    /// <c>DiscoveryCollector</c> writes 'delisted' over anything missing from enough consecutive
    /// passes, so silent omissions become a lifecycle event the venue never announced. Throwing
    /// fails the pass before the delisting sweep runs, so nothing is written and a person is asked.
    ///
    /// TRADING and BREAK are the only two values the live venue carried when this was captured —
    /// 1 365 and 2 333 rows.
    /// </summary>
    private static InstrumentStatus Status(BinanceSpotSymbol s) => s.Status switch
    {
        "TRADING" => InstrumentStatus.Trading,
        "BREAK" => InstrumentStatus.Halted,
        _ => throw new InvalidOperationException(
            $"Binance spot reported status '{s.Status}' for {s.Symbol}, which this adapter has never "
            + "seen; discovery stops rather than guessing whether it means halted or delisted"),
    };

    private static decimal Required(string? value, BinanceSpotSymbol s, string what) =>
        Decimal(value) ?? throw new InvalidOperationException(
            $"Binance spot did not carry {what} for {s.Symbol}");

    private static decimal? Decimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static double? Parse(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && double.IsFinite(d)
            ? d
            : null;
}

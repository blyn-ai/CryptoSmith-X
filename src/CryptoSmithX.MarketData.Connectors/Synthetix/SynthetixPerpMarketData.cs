using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Synthetix;

/// <summary>
/// Synthetix perpetuals as an <see cref="IExchangeMarketData"/>.
///
/// <b>An order book, not an oracle pool — and that is a measurement that overturned the catalogue.</b>
/// 0057 seeded this segment as "Synthetix Perps V3 (Base)" with <c>market_model = oracle_vault</c>, by
/// the venue's construction as it was. What answers today at papi.synthetix.io is an off-chain matching
/// engine with a public book, best bid and ask, mark, index, funding and open interest (2026-09-13); the
/// owner moved the segment to <c>orderbook</c> on that measurement (0061). So every quote column here is
/// read off the book, and none of it is derived or badged.
///
/// <b>What comes from where.</b>
///
///   getMarketPrices (one call, the whole venue)  last, mark, index, the estimated funding rate, 24h
///                                                base and quote volume, open interest, the venue clock
///   getOrderbook (per market)                    bid, ask and their sizes — as ONE frame — and depth
///   getLastTrades (per market)                   the instant of the last trade
///   getFundingRate (per market)                  the settled rate in force, the next settlement, the
///                                                interval
///
/// The per-market routes are read on the gated depth pass and remembered for the snapshot.
///
/// <b>Liquidations are not collected, because the venue does not publish them.</b> The public tape
/// carries no liquidation marker and there is no liquidation route among the public info actions; the
/// only stream that reports liquidations is <c>subAccountUpdates</c>, which is an account's own
/// activity. That column stays empty rather than holding a zero nobody counted.
/// </summary>
public sealed class SynthetixPerpMarketData : IExchangeMarketData
{
    private readonly SynthetixClient _client;
    private readonly RestTape _tape = new();
    private readonly ConcurrentDictionary<string, BookTop> _top = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SynthetixFundingRate> _funding = new(StringComparer.Ordinal);

    public SynthetixPerpMarketData(SynthetixClient client) => _client = client;

    public string SegmentCode => "synthetix-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        // open_interest is absent: the venue publishes the current figure (it reaches the snapshot
        // every pass) and no series behind it. liquidations is absent: see the class remarks.
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var markets = await _client.GetMarketsAsync(ct);

        var list = new List<Instrument>(markets.Count);
        foreach (var m in markets)
        {
            if (m.BaseAsset is not { Length: > 0 } b || m.QuoteAsset is not { Length: > 0 } q)
            {
                continue;
            }

            // The interval is published per market beside the rate, in milliseconds — asked for here
            // so discovery states what the venue says rather than what every captured market happened
            // to say (one hour).
            short? intervalHours = null;
            try
            {
                var funding = await _client.GetFundingRateAsync(m.Symbol, ct);
                _funding[m.Symbol] = funding;
                if (funding.FundingIntervalMs is { } ms && ms > 0 && ms % 3_600_000 == 0)
                {
                    intervalHours = (short)(ms / 3_600_000);
                }
            }
            catch (HttpRequestException)
            {
            }

            list.Add(new Instrument(
                ExchangeSymbol: m.Symbol,
                BaseAssetRaw: b,
                QuoteAssetRaw: q,
                ContractMultiplier: m.ContractSize is { } cs && cs > 0 ? cs : 1m,
                PriceStep: Decimal(m.PriceIncrement),
                QtyStep: Decimal(m.OrderSizeIncrement),
                MinQty: Decimal(m.MinOrderSize),
                // "0" on every market captured: the venue sets no minimum, which is null, not zero.
                MinNotional: Decimal(m.MinNotionalValue) is { } mn && mn > 0 ? mn : null,
                FundingIntervalHours: intervalHours,
                ListedAt: null,
                Status: m.IsOpen == true && m.IsCloseOnly != true ? InstrumentStatus.Trading : InstrumentStatus.Halted,
                RawJson: JsonSerializer.Serialize(m)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var prices = await _client.GetPricesAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(prices.Count);
        foreach (var (symbol, p) in prices)
        {
            var top = _top.GetValueOrDefault(symbol);
            var funding = _funding.GetValueOrDefault(symbol);

            list.Add(new Ticker(
                ExchangeSymbol: symbol,
                ReceivedAt: now,
                LastPrice: Num(p.LastPrice),
                LastTradeAt: _tape.Last(symbol)?.At,
                // Bid, ask and both sizes from ONE book frame, so a size always belongs to the price
                // beside it. The bulk route's own best bid and ask are fresher but carry no size; they
                // stand in only before the first book is read, with the sizes left empty rather than
                // borrowed from a different moment.
                BidPrice: top?.BidPrice ?? Num(p.BestBid),
                AskPrice: top?.AskPrice ?? Num(p.BestAsk),
                BidSize: top?.BidSize,
                AskSize: top?.AskSize,
                MarkPrice: Num(p.MarkPrice),
                IndexPrice: Num(p.IndexPrice),
                // The rate in force is the last one SETTLED. The number this route calls fundingRate is
                // the estimate for the period still accruing, and it goes to the column for that.
                FundingRate: Num(funding?.LastSettlementRate),
                FundingRatePredicted: Num(p.FundingRate),
                NextFundingAt: funding?.NextFundingTime is { } next && next > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(next)
                    : null,
                Turnover24h: Num(p.QuoteVolume24h),
                Volume24hBase: Num(p.Volume24h),
                OpenInterest: Num(p.OpenInterest),
                OpenInterestAt: now,
                Depth: null,
                VenueTs: p.Timestamp is { } ts && ts > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ts) : null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = (await _client.GetCandlesAsync(exchangeSymbol, from, to, ct)).Candles ?? [];

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTime);
            if (openTime + TimeSpan.FromMinutes(1) > to
                || Num(c.OpenPrice) is not { } o || Num(c.HighPrice) is not { } h
                || Num(c.LowPrice) is not { } l || Num(c.ClosePrice) is not { } cl)
            {
                continue;
            }

            list.Add(new Candle(exchangeSymbol, openTime, o, h, l, cl,
                Volume: Num(c.Volume) ?? 0, TradeCount: c.TradeCount, VolumeQuote: Num(c.QuoteVolume)));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = (await _client.GetFundingHistoryAsync(exchangeSymbol, from, to, ct)).FundingRates ?? [];

        var list = new List<FundingRate>(rows.Count);
        foreach (var r in rows)
        {
            if (r.FundingTime is { } ms && ms > 0 && Num(r.FundingRate) is { } rate)
            {
                list.Add(new FundingRate(exchangeSymbol, DateTimeOffset.FromUnixTimeMilliseconds(ms), rate));
            }
        }

        return list;
    }

    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var book = await _client.GetBookAsync(exchangeSymbol, ct);
        var now = DateTimeOffset.UtcNow;

        var bids = Levels(book.Bids);
        var asks = Levels(book.Asks);
        if (bids.Count > 0 && asks.Count > 0)
        {
            var bestBid = bids.MaxBy(l => l.Price);
            var bestAsk = asks.MinBy(l => l.Price);
            _top[exchangeSymbol] = new BookTop(bestBid.Price, bestBid.Qty, bestAsk.Price, bestAsk.Qty);
        }

        try
        {
            Fold(exchangeSymbol, (await _client.GetTradesAsync(exchangeSymbol, ct)).Trades ?? []);
        }
        catch (HttpRequestException)
        {
        }

        try
        {
            _funding[exchangeSymbol] = await _client.GetFundingRateAsync(exchangeSymbol, ct);
        }
        catch (HttpRequestException)
        {
        }

        // The route carries no timestamp; the band is stamped with when we read it.
        return DepthMath.Compute(bids, asks, now);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    private void Fold(string exchangeSymbol, IReadOnlyList<SynthetixTrade> trades)
    {
        var events = new List<TradeEvent>(trades.Count);

        // Oldest first: the venue lists newest first, and the tape keeps the last print it is shown
        // among prints that share an instant.
        foreach (var t in trades.Reverse())
        {
            if (t.TradeId is not { Length: > 0 } id || Num(t.Price) is not { } price || price <= 0
                || Num(t.Quantity) is not { } qty || t.Timestamp is not { } ms || ms <= 0)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            var sideIsSell = string.Equals(t.Side, "sell", StringComparison.OrdinalIgnoreCase);

            // `side` names whichever party `isMaker` describes. Every public print captured says
            // isMaker: false, so it is the taker's; a maker-side print is turned round rather than
            // trusted to mean the same thing.
            var takerSells = t.IsMaker == true ? !sideIsSell : sideIsSell;

            // No trade type: the public tape does not classify prints, and 0032 asks for null rather
            // than a guessed 'fill'.
            events.Add(new TradeEvent(exchangeSymbol, at, id, null, price, qty, takerSells ? "sell" : "buy", null));
            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    private static List<(double Price, double Qty)> Levels(IReadOnlyList<string[]>? raw)
    {
        var levels = new List<(double Price, double Qty)>(raw?.Count ?? 0);
        foreach (var l in raw ?? [])
        {
            if (l.Length >= 2 && Num(l[0]) is { } px && px > 0 && Num(l[1]) is { } qty && qty > 0)
            {
                levels.Add((px, qty));
            }
        }

        return levels;
    }

    private static decimal? Decimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private sealed record BookTop(double BidPrice, double BidSize, double AskPrice, double AskSize);
}

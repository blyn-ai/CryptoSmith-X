using System.Globalization;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

/// <summary>
/// Kraken Futures as an <see cref="IExchangeMarketData"/>. A dumb translator: it maps the venue's
/// wire format to the canonical records and does no normalisation of its own — the raw base is the
/// venue's symbol spelling (XBT, not BTC), which the Hub's asset_alias table resolves. Unit
/// conversions follow the DDL: funding is a fraction of notional per interval, turnover is in the
/// quote asset, open interest and candle volume are in the base asset, and Kraken reports no trade
/// count so candles carry null there.
///
/// Live market (tickers + book depth) is served from a WebSocket feed when one is wired; when the
/// feed is unhealthy the adapter transparently falls back to the REST call, so a dropped socket is a
/// coarser cadence, not an outage. Instruments, candles and funding history are always REST — they
/// are bootstrap, history and metadata. No retry/logging/sleeping on the REST path: an error
/// propagates and the collector loop counts it.
/// </summary>
public sealed class KrakenFuturesMarketData : IExchangeMarketData
{
    // V1 scope: linear, USD-quoted, hourly-funded flexible perpetuals. Inverse (PI_) and dated
    // (FI_/FF_) contracts have other symbol prefixes and are skipped in discovery.
    private const string PerpPrefix = "PF_";
    private const short FundingIntervalHours = 1;

    private readonly KrakenFuturesClient _client;
    private readonly IKrakenLiveFeed? _ws;

    public KrakenFuturesMarketData(KrakenFuturesClient client, IKrakenLiveFeed? ws = null)
    {
        _client = client;
        _ws = ws;
    }

    public string ExchangeCode => "kraken-futures";

    // Snapshot and depth are WS-first with a REST fallback whenever a feed is wired (see the ctor);
    // both transports are honest regardless of whether _ws happens to be null right now, since that
    // is a config fact (ws_url set or not), not a per-request coin flip.
    public IReadOnlyList<CollectionCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest,ws"),
        new("depth", "rest,ws"),
        new("candles", "rest"),
        new("funding", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var instruments = await _client.GetInstrumentsAsync(ct);

        var list = new List<Instrument>();
        foreach (var k in instruments)
        {
            if (!k.Symbol.StartsWith(PerpPrefix, StringComparison.Ordinal) || k.IsExpired)
            {
                continue;
            }

            var qtyStep = QtyStep(k.ContractValueTradePrecision);
            list.Add(new Instrument(
                ExchangeSymbol: k.Symbol,
                // Raw base exactly as the symbol spells it — PF_XBTUSD → "XBT", not Kraken's own
                // normalised `base` field ("BTC"). The seeded XBT→BTC alias resolves it downstream.
                BaseAssetRaw: RawBase(k.Symbol, k.Quote),
                QuoteAssetRaw: k.Quote,
                ContractMultiplier: k.ContractSize,
                PriceStep: k.TickSize,
                QtyStep: qtyStep,
                // Kraken states a quantity precision but no explicit minimum, so the smallest
                // tradable increment stands in for one; it never defines a minimum notional.
                MinQty: qtyStep,
                MinNotional: null,
                FundingIntervalHours: FundingIntervalHours,
                ListedAt: k.OpeningDate,
                Status: Status(k),
                RawJson: k.RawJson));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        // WS first: a fresh cache slice (with depth from the live book). Only fresh symbols come
        // back, so a frozen entry ages its snapshot row instead of masquerading as current.
        if (_ws is not null && _ws.TryGetFreshTickers(out var live))
        {
            return live;
        }

        // Degraded / no WS: the REST /tickers call, exactly as before. Depth is filled separately.
        var response = await _client.GetTickersAsync(ct);
        var at = response.ServerTime;

        var list = new List<Ticker>();
        foreach (var t in response.Tickers)
        {
            if (!t.Symbol.StartsWith(PerpPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            list.Add(new Ticker(
                ExchangeSymbol: t.Symbol,
                ReceivedAt: at,
                LastPrice: t.Last,
                BidPrice: t.Bid,
                AskPrice: t.Ask,
                BidSize: t.BidSize,
                AskSize: t.AskSize,
                MarkPrice: t.MarkPrice,
                IndexPrice: t.IndexPrice,
                // Kraken's ticker funding rate is absolute (quote per contract); the schema wants the
                // fraction of notional per interval, so divide by mark. (The funding *history*
                // endpoint already returns a relative rate and is used as-is in GetFundingHistory.)
                FundingRate: t.MarkPrice > 0 ? t.FundingRate / t.MarkPrice : 0,
                Turnover24h: t.VolumeQuote,
                OpenInterest: t.OpenInterest,
                // Kraken serves OI in the same call as the ticker, so it shares its timestamp.
                OpenInterestAt: at,
                // The book is a separate per-symbol call; see GetOrderBookAsync and DepthCollector.
                Depth: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var response = await _client.GetCandles1mAsync(
            exchangeSymbol, from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds(), ct);

        var list = new List<Candle>();
        foreach (var c in response.Candles)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(c.Time);

            // Closed bars only: a 1-minute bar opened at T closes at T+60 s, so the one covering `to`
            // is still forming and is dropped.
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Parse(c.Open),
                High: Parse(c.High),
                Low: Parse(c.Low),
                Close: Parse(c.Close),
                Volume: Parse(c.Volume),
                TradeCount: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var response = await _client.GetFundingHistoryAsync(exchangeSymbol, ct);

        // The v4 endpoint returns the whole series oldest-first with no time bounds, so window it here.
        var list = new List<FundingRate>();
        foreach (var r in response.Rates)
        {
            if (r.Timestamp < from || r.Timestamp > to)
            {
                continue;
            }

            list.Add(new FundingRate(
                ExchangeSymbol: exchangeSymbol,
                FundingTime: r.Timestamp,
                Rate: r.RelativeFundingRate));
        }

        return list;
    }

    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        // WS first: depth off the live book. Falls through to REST when the book is dirty or stale.
        if (_ws is not null && _ws.TryGetDepth(exchangeSymbol, out var live))
        {
            return live;
        }

        var response = await _client.GetOrderBookAsync(exchangeSymbol, ct);
        var bids = Array.ConvertAll(response.OrderBook.Bids, l => (l[0], l[1]));
        var asks = Array.ConvertAll(response.OrderBook.Asks, l => (l[0], l[1]));
        return DepthMath.Compute(bids, asks, response.ServerTime);
    }

    /// <summary>PF_XBTUSD with quote USD → "XBT": strip the prefix and the trailing quote.</summary>
    private static string RawBase(string symbol, string quote)
    {
        var core = symbol[PerpPrefix.Length..];
        return core.EndsWith(quote, StringComparison.Ordinal) ? core[..^quote.Length] : core;
    }

    private static InstrumentStatus Status(KrakenInstrument k) =>
        !k.Tradeable ? InstrumentStatus.Halted
        : k.PostOnly ? InstrumentStatus.PostOnly
        : InstrumentStatus.Trading;

    private static decimal QtyStep(int precision)
    {
        var step = 1m;
        for (var i = 0; i < precision; i++)
        {
            step /= 10m;
        }

        return step;
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}

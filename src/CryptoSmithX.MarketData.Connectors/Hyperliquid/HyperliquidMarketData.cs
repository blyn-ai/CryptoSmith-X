using System.Globalization;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>
/// Hyperliquid as an <see cref="IExchangeMarketData"/>. A dumb translator: the venue's symbols are
/// already bare coin names ("BTC", "kPEPE"), so <see cref="Instrument.BaseAssetRaw"/> is the symbol
/// itself — the Hub's asset_alias table resolves the k-prefixed ones (kPEPE → PEPE ×1000, seeded
/// alongside 0006's existing kPEPE alias). Quote is hardcoded "USD": Hyperliquid perps are USD-quoted,
/// USDC-margined, and the venue's own API never spells out a quote asset field to normalise.
///
/// The API's one real gap: <c>metaAndAssetCtxs</c> batches mark/oracle/funding/OI/volume for every
/// coin in a single call, but carries no book at all — not even a last-trade price. Bid/ask/size (and
/// depth) come from <see cref="IHyperliquidLiveFeed"/> instead, which the Hub wires to either the REST
/// polling baseline (<see cref="HyperliquidBookFeed"/>, always available) or the live socket
/// (<see cref="HyperliquidWsFeed"/>, preferred when healthy) — see <c>ExchangeWorker.BuildHyperliquid</c>.
/// A coin missing a fresh book sample is simply omitted from the ticker batch, same as WEEX's open
/// interest: its snapshot row goes stale honestly rather than being written with a fabricated spread.
/// No retry/logging/sleeping on the REST path: an error propagates and the collector loop counts it.
/// </summary>
public sealed class HyperliquidMarketData : IExchangeMarketData
{
    private const string Quote = "USD";
    private const short FundingIntervalHours = 1;

    // Perp prices are capped at 5 significant figures AND at (6 - szDecimals) decimal places,
    // whichever is stricter (https://hyperliquid.gitbook.io/hyperliquid-docs — "Tick and lot size").
    // The sig-fig cap floats with price magnitude and has no single fixed step; PriceStep here is the
    // coarser, constant decimal-place cap, which is what the schema's column can hold. This adapter
    // only records observed prices, never places orders, so the tighter floating cap is not needed.
    private const int MaxDecimals = 6;

    private readonly HyperliquidClient _client;
    private readonly IHyperliquidLiveFeed _restFeed;
    private readonly IHyperliquidLiveFeed? _wsFeed;

    public HyperliquidMarketData(HyperliquidClient client, IHyperliquidLiveFeed restFeed, IHyperliquidLiveFeed? wsFeed = null)
    {
        _client = client;
        _restFeed = restFeed;
        _wsFeed = wsFeed;
    }

    public string ExchangeCode => "hyperliquid";

    // Snapshot needs the live feed for bid/ask/size (metaAndAssetCtxs carries no book at all), and
    // depth is served off the same feed — both honestly "rest,ws" once a ws_url is configured, since
    // the REST book cycler always runs as the baseline (see HyperliquidBookFeed).
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
        var meta = await _client.GetMetaAsync(ct);

        var list = new List<Instrument>(meta.Universe.Count);
        foreach (var u in meta.Universe)
        {
            // isDelisted is the venue's own signal, unlike WEEX's silent zero-price/zero-volume
            // symbols — nothing more to infer here.
            if (u.IsDelisted)
            {
                continue;
            }

            var qtyStep = Step(u.SzDecimals);
            list.Add(new Instrument(
                ExchangeSymbol: u.Name,
                BaseAssetRaw: u.Name,
                QuoteAssetRaw: Quote,
                ContractMultiplier: 1m,
                PriceStep: Step(Math.Max(0, MaxDecimals - u.SzDecimals)),
                QtyStep: qtyStep,
                // No explicit minimum on /meta; the smallest tradable increment stands in, same
                // reasoning Kraken uses.
                MinQty: qtyStep,
                MinNotional: null,
                FundingIntervalHours: FundingIntervalHours,
                // No listing-date field on /meta.
                ListedAt: null,
                Status: InstrumentStatus.Trading,
                RawJson: RawJson(u)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var (meta, ctxs) = await _client.GetMetaAndAssetCtxsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(meta.Universe.Count);
        for (var i = 0; i < meta.Universe.Count && i < ctxs.Count; i++)
        {
            var u = meta.Universe[i];
            if (u.IsDelisted)
            {
                continue;
            }

            var c = ctxs[i];

            // No last-trade-price field exists at this batch scale; the book mid is the closest
            // honest proxy Hyperliquid's own info endpoint offers (see the class doc comment).
            if (c.MidPx is not { } midText || !double.TryParse(midText, CultureInfo.InvariantCulture, out var last) || last <= 0)
            {
                continue;
            }

            if (!TryGetTop(u.Name, out var top))
            {
                continue;   // neither live feed has a fresh book sample for this coin yet
            }

            list.Add(new Ticker(
                ExchangeSymbol: u.Name,
                ReceivedAt: now,
                LastPrice: last,
                BidPrice: top.BidPrice,
                AskPrice: top.AskPrice,
                BidSize: top.BidSize,
                AskSize: top.AskSize,
                MarkPrice: Parse(c.MarkPx),
                IndexPrice: Parse(c.OraclePx),
                // Already the fraction of notional per interval — Hyperliquid's own semantics match
                // the schema directly, no rescale needed (unlike Kraken's absolute-rate ticker).
                FundingRate: Parse(c.Funding),
                Turnover24h: Parse(c.DayNtlVlm),
                OpenInterest: Parse(c.OpenInterest),
                // Batched with the ticker call itself, so it shares its timestamp — same as Kraken.
                OpenInterestAt: now,
                // The book is served from the live feed; see GetOrderBookAsync and DepthCollector.
                Depth: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetCandles1mAsync(exchangeSymbol, from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds(), ct);

        var list = new List<Candle>(rows.Count);
        foreach (var r in rows)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(r.OpenTimeMs);

            // Closed bars only: the one covering `to` is still forming.
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Parse(r.Open),
                High: Parse(r.High),
                Low: Parse(r.Low),
                Close: Parse(r.Close),
                Volume: Parse(r.Volume),
                TradeCount: r.TradeCount));
        }

        list.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetFundingHistoryAsync(exchangeSymbol, from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds(), ct);

        var list = new List<FundingRate>(rows.Count);
        foreach (var r in rows)
        {
            list.Add(new FundingRate(
                ExchangeSymbol: exchangeSymbol,
                FundingTime: DateTimeOffset.FromUnixTimeMilliseconds(r.Time),
                Rate: Parse(r.FundingRate)));
        }

        list.Sort((a, b) => a.FundingTime.CompareTo(b.FundingTime));
        return list;
    }

    public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        if (TryGetDepth(exchangeSymbol, out var depth))
        {
            return Task.FromResult<Depth?>(depth);
        }

        // Neither live feed has reached this coin yet; the row simply ages instead of a blocking
        // one-off REST call racing the background cycle that will fill it shortly.
        return Task.FromResult<Depth?>(null);
    }

    private bool TryGetTop(string symbol, out BookTop top) =>
        (_wsFeed?.TryGetTop(symbol, out top) ?? false) || _restFeed.TryGetTop(symbol, out top);

    private bool TryGetDepth(string symbol, out Depth depth) =>
        (_wsFeed?.TryGetDepth(symbol, out depth) ?? false) || _restFeed.TryGetDepth(symbol, out depth);

    /// <summary>10^-decimalPlaces as a decimal step.</summary>
    private static decimal Step(int decimalPlaces)
    {
        var step = 1m;
        for (var i = 0; i < decimalPlaces; i++)
        {
            step /= 10m;
        }

        return step;
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static string RawJson(HlUniverseEntry u) =>
        string.Create(CultureInfo.InvariantCulture,
            $$"""{"name":"{{u.Name}}","szDecimals":{{u.SzDecimals}},"isDelisted":{{(u.IsDelisted ? "true" : "false")}}}""");
}

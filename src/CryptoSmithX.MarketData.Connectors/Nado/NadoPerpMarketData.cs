using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Nado;

/// <summary>
/// Nado perpetuals on Ink L2 as an <see cref="IExchangeMarketData"/> — an order book, and its own venue.
///
/// <b>Not Vertex renamed.</b> Vertex Protocol closed in July 2025 and its team and stack moved to Ink,
/// where they run Nado. Different chain, different contracts, liquidity built from nothing: carrying it
/// under Vertex's code would assert a continuity there is not. 0062 disables vertex-perp with that reason
/// and seeds nado-perp as its own row.
///
/// <b>What comes from where.</b>
///
///   archive /v2/contracts (one call)   last, mark, index, 24h base and quote volume, open interest, the
///                                      next funding instant
///   gateway /v2/orderbook (per market) bid, ask and their sizes as one frame, depth, the book's clock
///   archive /v2/trades (per market)    the instant of the last trade — and only that
///   archive funding_rate_history       the realised HOURLY rate in force
///   archive events                     liquidations
///
/// <b>The tape is not collected, and that is the venue's shape rather than a gap.</b> Every match appears
/// twice on /v2/trades, once per side, each leg priced with its own fee folded in. Neither leg says which
/// was the taker and neither carries the execution price clean, so a stored print would be one of two
/// numbers for one event with a side nobody published. The last-trade instant, which both legs share, is
/// the one fact read off it.
///
/// <b>The quote is USDT0, and it stays USDT0.</b> It is the bridged USDT on Ink — a distinct token — so the
/// segment's quote list names it rather than an alias calling it USDT.
/// </summary>
public sealed class NadoPerpMarketData : IExchangeMarketData
{
    private const int LiquidationTxPage = 100;
    private const int MaxLiquidationPages = 10;

    private readonly NadoClient _client;
    private readonly ConcurrentDictionary<string, int> _productId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BookTop> _top = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTrade = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _settled = new(StringComparer.Ordinal);

    public NadoPerpMarketData(NadoClient client) => _client = client;

    public string SegmentCode => "nado-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("depth", "rest"),
        new("liquidations", "rest"),
        // trades is absent: see the class remarks.
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var contracts = await _client.GetContractsAsync(ct);
        var symbols = (await _client.GetSymbolsAsync(ct)).Data?.Symbols ?? new Dictionary<string, NadoSymbol>();
        var specByProduct = symbols.Values.Where(s => s.Type == "perp").ToDictionary(s => s.ProductId);

        var list = new List<Instrument>(contracts.Count);
        foreach (var c in contracts.Values)
        {
            if (c.ProductType != "perpetual" || c.BaseCurrency is not { Length: > 0 } baseCurrency
                || c.QuoteCurrency is not { Length: > 0 } quote)
            {
                continue;
            }

            _productId[c.TickerId] = c.ProductId;
            var spec = specByProduct.GetValueOrDefault(c.ProductId);

            list.Add(new Instrument(
                ExchangeSymbol: c.TickerId,
                // "BTC-PERP": the suffix names the product type, not the asset, and the page groups by the
                // asset — left on, this venue would sit on no BTC page at all.
                BaseAssetRaw: baseCurrency.EndsWith("-PERP", StringComparison.Ordinal) ? baseCurrency[..^5] : baseCurrency,
                QuoteAssetRaw: quote,
                ContractMultiplier: 1m,
                PriceStep: X18(spec?.PriceIncrementX18),
                QtyStep: X18(spec?.SizeIncrementX18),
                MinQty: null,
                // min_size is a QUOTE notional on this venue — 100 USDT0 on BTC-PERP — so it is the minimum
                // notional, and not a minimum quantity of a hundred bitcoin.
                MinNotional: X18(spec?.MinSizeX18),
                // The realised series settles hourly.
                FundingIntervalHours: 1,
                ListedAt: null,
                Status: InstrumentStatus.Trading,
                RawJson: JsonSerializer.Serialize(c)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var contracts = await _client.GetContractsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(contracts.Count);
        foreach (var c in contracts.Values)
        {
            if (c.ProductType != "perpetual")
            {
                continue;
            }

            var top = _top.GetValueOrDefault(c.TickerId);
            list.Add(new Ticker(
                ExchangeSymbol: c.TickerId,
                ReceivedAt: now,
                LastPrice: Fin(c.LastPrice),
                LastTradeAt: _lastTrade.TryGetValue(c.TickerId, out var lt) ? lt : null,
                // One book frame, so a size belongs to the price beside it; null until the book is read.
                BidPrice: top?.BidPrice,
                AskPrice: top?.AskPrice,
                BidSize: top?.BidSize,
                AskSize: top?.AskSize,
                MarkPrice: Fin(c.MarkPrice),
                IndexPrice: Fin(c.IndexPrice),
                // The rate in force is the last REALISED hourly rate. The contracts route's funding_rate is the
                // same rate restated per 24 hours, and dividing it here would present our arithmetic as the
                // venue's settlement.
                FundingRate: _settled.TryGetValue(c.TickerId, out var rate) ? rate : null,
                NextFundingAt: c.NextFundingRateTimestamp is { } nf && nf > 0 ? DateTimeOffset.FromUnixTimeSeconds(nf) : null,
                Turnover24h: Fin(c.QuoteVolume),
                Volume24hBase: Fin(c.BaseVolume),
                OpenInterest: Fin(c.OpenInterest),
                OpenInterestAt: now,
                OiQuote: Fin(c.OpenInterestUsd),
                Depth: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!_productId.TryGetValue(exchangeSymbol, out var product))
        {
            return [];
        }

        var minutes = (int)Math.Clamp((to - from).TotalMinutes + 1, 1, 1000);
        var rows = (await _client.GetCandlesAsync(product, to, minutes, ct)).Candlesticks ?? [];

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            if (!long.TryParse(c.Timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeSeconds(s);
            if (openTime < from || openTime + TimeSpan.FromMinutes(1) > to
                || X18d(c.OpenX18) is not { } o || X18d(c.HighX18) is not { } h
                || X18d(c.LowX18) is not { } l || X18d(c.CloseX18) is not { } cl)
            {
                continue;
            }

            // Volume is the absolute base amount filled, x18. No quote volume on this route.
            list.Add(new Candle(exchangeSymbol, openTime, o, h, l, cl, Volume: X18d(c.VolumeX18) ?? 0, TradeCount: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!_productId.TryGetValue(exchangeSymbol, out var product))
        {
            return [];
        }

        var rows = (await _client.GetFundingHistoryAsync(product, from, 500, ct)).FundingRates ?? [];

        var list = new List<FundingRate>(rows.Count);
        DateTimeOffset? newest = null;
        foreach (var r in rows)
        {
            if (!long.TryParse(r.Timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                || X18d(r.FundingRateX18) is not { } rate)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeSeconds(s);
            if (newest is null || at > newest)
            {
                newest = at;
                _settled[exchangeSymbol] = rate;
            }

            if (at >= from && at <= to)
            {
                list.Add(new FundingRate(exchangeSymbol, at, rate));
            }
        }

        return list;
    }

    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var book = await _client.GetBookAsync(exchangeSymbol, ct);
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
            var trades = await _client.GetTradesAsync(exchangeSymbol, 1, ct);
            if (trades.Count > 0 && trades.Max(t => t.Timestamp ?? 0) is var s and > 0)
            {
                _lastTrade[exchangeSymbol] = DateTimeOffset.FromUnixTimeSeconds(s);
            }
        }
        catch (HttpRequestException)
        {
        }

        // The book carries its own clock; used when present.
        var at = book.Timestamp is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow;
        return DepthMath.Compute(bids, asks, at);
    }

    /// <summary>
    /// Liquidated size per hour on one product, from the archive's <c>liquidate_subaccount</c> events.
    ///
    /// <b>One liquidation, counted once.</b> A liquidation moves two balances — the liquidatee's position
    /// shrinks and the liquidator's grows by the same amount — and both arrive as events under one
    /// submission. The size is the largest change in this product's perp balance across that submission's
    /// events, not their sum. A submission that did not move this product's balance (a liquidation of a
    /// different product or of spot health, which the product filter still returns) contributes nothing.
    ///
    /// Hours are reported only where the backwards walk covered them, and an hour covered with no
    /// liquidation is a counted zero.
    /// </summary>
    public async Task<IReadOnlyList<LiquidationBucket>> GetLiquidationVolumeAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!_productId.TryGetValue(exchangeSymbol, out var product))
        {
            return [];
        }

        var sums = new Dictionary<DateTimeOffset, double>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? maxTime = null;
        var reached = to;
        var exhausted = false;

        for (var page = 0; page < MaxLiquidationPages; page++)
        {
            var body = await _client.GetLiquidationsAsync(product, maxTime, LiquidationTxPage, ct);
            var txs = body.Txs ?? [];
            if (txs.Count == 0)
            {
                exhausted = true;
                break;
            }

            var timeByIdx = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var tx in txs)
            {
                if (tx.SubmissionIdx is { } idx && long.TryParse(tx.Timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    var at = DateTimeOffset.FromUnixTimeSeconds(s);
                    timeByIdx[idx] = at;
                    if (at < reached)
                    {
                        reached = at;
                    }
                }
            }

            foreach (var group in (body.Events ?? []).Where(e => e.ProductId == product && e.SubmissionIdx is not null)
                                                       .GroupBy(e => e.SubmissionIdx!))
            {
                if (!seen.Add(group.Key) || !timeByIdx.TryGetValue(group.Key, out var at) || at < from || at > to)
                {
                    continue;
                }

                var size = group.Max(e => Math.Abs((PerpAmount(e.PostBalance) ?? 0) - (PerpAmount(e.PreBalance) ?? 0)));
                var hour = FloorHour(at);
                sums[hour] = sums.GetValueOrDefault(hour) + size;
            }

            if (reached <= from || txs.Count < LiquidationTxPage)
            {
                exhausted = txs.Count < LiquidationTxPage;
                break;
            }

            maxTime = reached;
        }

        var firstWhole = exhausted ? FloorHour(from) : FloorHour(reached) + TimeSpan.FromHours(1);
        if (firstWhole < FloorHour(from))
        {
            firstWhole = FloorHour(from);
        }

        var list = new List<LiquidationBucket>();
        for (var hour = firstWhole; hour <= FloorHour(to); hour += TimeSpan.FromHours(1))
        {
            list.Add(new LiquidationBucket(exchangeSymbol, 3600, hour, sums.GetValueOrDefault(hour), "base"));
        }

        return list;
    }

    private static double? PerpAmount(JsonElement balance) =>
        balance.ValueKind == JsonValueKind.Object
        && balance.TryGetProperty("perp", out var perp)
        && perp.TryGetProperty("balance", out var b)
        && b.TryGetProperty("amount", out var amount)
            ? X18d(amount.GetString())
            : null;

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

    /// <summary>An x18 integer string as its value. decimal holds the 23-digit prices exactly before the
    /// division, where a double would round the integer first.</summary>
    private static decimal? X18(string? s) =>
        decimal.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v / 1_000_000_000_000_000_000m : null;

    private static double? X18d(string? s) => X18(s) is { } d ? (double)d : null;

    private static double? Fin(double? v) => v is { } d && double.IsFinite(d) ? d : null;

    private static DateTimeOffset FloorHour(DateTimeOffset at) =>
        DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / 3600 * 3600);

    private sealed record BookTop(double BidPrice, double BidSize, double AskPrice, double AskSize);
}

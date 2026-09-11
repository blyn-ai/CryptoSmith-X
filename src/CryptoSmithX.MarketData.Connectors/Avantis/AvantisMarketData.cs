using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// Avantis as an <see cref="IExchangeMarketData"/> — a perp DEX on Base whose counterparty is a
/// liquidity pool rather than a resting order, and whose price comes from an oracle rather than its
/// own matching. The first venue here with <c>market_model = 'oracle_vault'</c>, and the first with
/// no book at all.
///
/// <b>What it does not fill, and why that is the point.</b> Bid, ask, their sizes and every depth
/// band stay NULL because this market has no second side — not because a feed went quiet.
/// <c>funding_rate</c> stays NULL beside a NULL interval because there is no discrete payment here,
/// only a continuous carry whose components are a different quantity with a different unit. Both are
/// read through <c>segment.market_model</c>, which is what turns "empty" into "empty by nature".
///
/// <b>And it fills no price either — measured, not assumed.</b> The catalogue has no price field at
/// all, and the venue's own Socket.IO broadcast was checked live on 2026-09-11: 86 KB of
/// <c>RES:DATA</c> carrying pairInfos, with no occurrence of price, index, mid or last anywhere in
/// it. Price on this venue exists only on the oracle. The one place Avantis itself serves it is the
/// candle shim, and those are minute bars of the ORACLE's history — which is why they land as
/// <c>series = 'index'</c> price candles and never as this row's <c>index_price</c>. A row of OI,
/// fees and trading hours with no price is an honest row; a price copied off a stream the venue
/// does not publish would not be.
/// </summary>
public sealed class AvantisMarketData : IExchangeMarketData
{
    private readonly AvantisClient _client;
    private readonly IAvantisCatalogFeed? _ws;

    public AvantisMarketData(AvantisClient client, IAvantisCatalogFeed? ws = null)
    {
        _client = client;
        _ws = ws;
    }

    public string SegmentCode => "avantis-perp";

    /// <summary>
    /// Only what this venue honestly serves. DEPTH IS ABSENT ON PURPOSE — the precedent is the
    /// fake adapter, which declares none because <c>GetOrderBookAsync</c> always answers null, and
    /// declaring it would start a <c>DepthCollector</c> for a market that has no book to collect.
    /// Trades and liquidations are absent for the same reason: no public tape exists on any
    /// endpoint. Candles are declared as <c>candles_index</c> only — the shim's bars are the
    /// oracle's price and carry no volume, so they are not market candles.
    ///
    /// <c>snapshot</c> is "rest,ws": the catalogue arrives either way, and the socket is the venue's
    /// own rather than the oracle's — verified by connecting to it.
    /// </summary>
    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest,ws"),
        new("candles_index", "rest"),
        new("vault_pair_state", "rest,ws"),
        new("vault_state", "rest"),
        new("spec_versions", "rest"),
    ];

    /// <summary>Linear USD-quoted perpetuals across every asset class the venue lists — crypto,
    /// fx, metals, commodities, equities and indices all share one segment, because they share the
    /// host, the socket, the symbol space, the adapter and the budget. The class is a property of
    /// the instrument, not a reason for a second segment.</summary>
    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var catalog = Fresh() ?? new AvantisCatalog(await _client.GetTradingRawAsync(ct), DateTimeOffset.UtcNow);
        var typed = catalog.Typed?.PairInfos ?? [];
        var list = new List<Instrument>(typed.Count);

        // Walked from the RAW pairs, not the typed ones, because the specification stored for each
        // instrument is the venue's own payload — minus its observations, or every discovery pass
        // would mint a new specification version for every instrument. See AvantisSpec.
        foreach (var (symbol, raw) in catalog.RawPairs())
        {
            var p = typed.Values.FirstOrDefault(x => string.Equals(Symbol(x), symbol, StringComparison.Ordinal));
            if (p?.From is not { Length: > 0 } || p.To is not { Length: > 0 })
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: symbol,
                BaseAssetRaw: p.From,
                QuoteAssetRaw: p.To,
                // A real fact about the contract rather than a gap, so it is not nullable and not
                // null: one unit of exposure is one unit of the base asset here.
                ContractMultiplier: 1m,
                // NULL, not zero. The tick is the ORACLE's and this venue publishes no grid of its
                // own — the only thing it constrains is the notional, below. A zero would read as a
                // measured step of zero, which is a claim nobody made; 0045 made the absence
                // expressible after discovery failed on the CHECK that forbade both.
                PriceStep: null,
                QtyStep: null,
                MinQty: null,
                MinNotional: p.MinLevPosUsdc is { } min ? (decimal)min : null,
                // No discrete funding payment here, so the pair (rate, interval) stays
                // (NULL, NULL) and reads unambiguously under market_model. 0028 needs no change.
                FundingIntervalHours: null,
                ListedAt: null,
                // Delisted and placeholder entries answer isPairListed = false — 27 of 120 did on
                // the day this was written, and one of them had no symbol at all.
                Status: p.IsPairListed ? InstrumentStatus.Trading : InstrumentStatus.Delisted,
                RawJson: AvantisSpec.StaticJson(raw)));
        }

        return list;
    }

    /// <summary>
    /// One batched call for the whole venue — no per-symbol sweep exists or is needed.
    ///
    /// A CLOSED instrument is returned WITH <c>MarketOpen = false</c> rather than omitted. Dropping
    /// it would freeze its <c>received_at</c>, and the page would read a market that is shut over
    /// the weekend as a feed that died — which is exactly the confusion this column was added to
    /// prevent.
    /// </summary>
    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var catalog = Fresh() ?? new AvantisCatalog(await _client.GetTradingRawAsync(ct), DateTimeOffset.UtcNow);
        var trading = catalog.Typed;

        var now = DateTimeOffset.UtcNow;
        var list = new List<Ticker>(trading?.PairInfos?.Count ?? 0);

        foreach (var p in trading?.PairInfos?.Values ?? Enumerable.Empty<AvPair>())
        {
            if (p.From is not { Length: > 0 } || p.To is not { Length: > 0 })
            {
                continue;
            }

            list.Add(new Ticker(
                ExchangeSymbol: Symbol(p),
                ReceivedAt: now,
                // Every price is absent on this venue's own surfaces. See the class remarks: this
                // is measured, and filling any of them would be inventing an observation.
                LastPrice: null,
                BidPrice: null,
                AskPrice: null,
                BidSize: null,
                AskSize: null,
                MarkPrice: null,
                IndexPrice: null,
                // No discrete payment: the carry is continuous and its components are a different
                // quantity. Writing a carry rate here would misstate the unit as well as the thing.
                FundingRate: null,
                Turnover24h: null,
                // Both units published, so both are written and neither is derived.
                OpenInterest: Sum(p.CoinOi),
                OpenInterestAt: now,
                Depth: null,
                OiQuote: p.PairOi ?? Sum(p.OpenInterest),
                MarketOpen: p.Feed?.Attributes?.IsOpen));
        }

        return list;
    }

    /// <summary>Null forever, and <c>depth</c> is not in <see cref="Capabilities"/> — the two
    /// together are what stop a depth loop starting for a market with no book.</summary>
    public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct) =>
        Task.FromResult<Depth?>(null);

    /// <summary>No market candles: the venue publishes no tape, so a bar of executions does not
    /// exist to fetch. The oracle's bars are served by
    /// <see cref="GetPriceCandles1mAsync"/> instead.</summary>
    public Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Candle>>([]);

    /// <summary>No funding history: there is no discrete payment to have a history of.</summary>
    public Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FundingRate>>([]);

    /// <summary>
    /// The ORACLE's closed minute bars, from the venue's own TradingView shim, addressed by the
    /// Pyth symbol the catalogue already carries on every pair.
    ///
    /// Only <c>index</c> is served. A mark series would need the venue to publish marking against
    /// an adjusted price, and it publishes no such thing; answering the index bars for a mark
    /// request would be relabelling one measurement as another.
    /// </summary>
    public async Task<IReadOnlyList<PriceCandle>> GetPriceCandles1mAsync(
        string exchangeSymbol, string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!string.Equals(series, "index", StringComparison.Ordinal))
        {
            return [];
        }

        var pyth = _ws?.TryGetPythSymbol(exchangeSymbol, out var cached) == true
            ? cached
            : (await PythSymbolAsync(exchangeSymbol, ct));
        if (pyth is null)
        {
            return [];
        }

        var bars = await _client.GetCandlesAsync(pyth, "1", from, to, ct);
        var list = new List<PriceCandle>(bars.Count);
        foreach (var b in bars)
        {
            var open = DateTimeOffset.FromUnixTimeMilliseconds(b.TimeMs);

            // Closed bars only — the one still forming is never returned, the same rule every
            // other adapter here applies to its own candles.
            if (open + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new PriceCandle(exchangeSymbol, series, open, b.Open, b.High, b.Low, b.Close));
        }

        return list;
    }

    /// <summary>Every pair's vault state, off the same catalogue the tickers come from — so this
    /// costs no extra call at all when the socket is healthy.</summary>
    public async Task<IReadOnlyList<VaultPairState>> GetVaultPairStateAsync(CancellationToken ct)
    {
        var catalog = Fresh() ?? new AvantisCatalog(await _client.GetTradingRawAsync(ct), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var list = new List<VaultPairState>();

        foreach (var p in catalog.Typed?.PairInfos?.Values ?? Enumerable.Empty<AvPair>())
        {
            if (p.From is not { Length: > 0 } || p.To is not { Length: > 0 })
            {
                continue;
            }

            list.Add(new VaultPairState(
                Symbol(p), now,
                OiLongBase: p.CoinOi?.Long,
                OiShortBase: p.CoinOi?.Short,
                OiLongQuote: p.OpenInterest?.Long,
                OiShortQuote: p.OpenInterest?.Short,
                OiMaxQuote: p.PairMaxOi,
                OiBlockLimit: p.BlockOiLimit,
                DepthAbove1Pct: p.PairParams?.OnePercentDepthAbove,
                DepthBelow1Pct: p.PairParams?.OnePercentDepthBelow,
                LiquidityBuy: p.Liquidity?.Buy,
                LiquiditySell: p.Liquidity?.Sell,
                PriceImpactMultiplier: p.PriceImpactMultiplier,
                SkewImpactMultiplier: p.SkewImpactMultiplier,
                SpreadPercent: p.SpreadP,
                DecayedVol: p.DecayedVol));
        }

        return list;
    }

    /// <summary>The ERC-4626 tranche, from the venue's own read endpoint. Its figures arrive as
    /// decimal strings in on-chain units — USDC at 1e6 and the ratio at 1e10, both from
    /// <c>GET /v2/meta</c> — and are scaled here rather than carried onward in a shape no reader
    /// could interpret.</summary>
    public async Task<VaultState?> GetVaultStateAsync(CancellationToken ct)
    {
        var s = await _client.GetVaultStateAsync(ct);
        if (s is null)
        {
            return null;
        }

        return new VaultState(
            DateTimeOffset.UtcNow,
            TotalAssetsQuote: AvantisClient.Scaled(s.TotalAssets, AvantisClient.UsdcScale),
            TotalSupplyShares: AvantisClient.Scaled(s.TotalSupply, AvantisClient.UsdcScale),
            SharePriceQuote: s.SharePriceUsdc,
            UtilizationRatio: AvantisClient.Scaled(s.UtilizationRatio, AvantisClient.RatioScale),
            DepositCapQuote: AvantisClient.Scaled(s.DepositCap, AvantisClient.UsdcScale),
            WithdrawThresholdQuote: AvantisClient.Scaled(s.WithdrawThreshold, AvantisClient.UsdcScale));
    }

    private async Task<string?> PythSymbolAsync(string exchangeSymbol, CancellationToken ct)
    {
        var trading = await _client.GetTradingAsync(ct);
        foreach (var p in trading.PairInfos?.Values ?? Enumerable.Empty<AvPair>())
        {
            if (string.Equals(Symbol(p), exchangeSymbol, StringComparison.Ordinal))
            {
                return p.Feed?.Attributes?.Symbol;
            }
        }

        return null;
    }

    /// <summary>The venue's own spelling, and the only one that is stable: the catalogue is keyed
    /// by a numeric index that can move, so the pair is addressed by from/to.</summary>
    internal static string Symbol(AvPair p) => $"{p.From}/{p.To}";

    /// <summary>The socket's catalogue while it is genuinely fresh, else nothing — the adapter then
    /// makes the one REST call. Same WS-first / REST-fallback shape the other four venues use.</summary>
    private AvantisCatalog? Fresh() => _ws?.TryGetCatalog(out var c) == true ? c : null;

    /// <summary>Long plus short. Verified against the venue's own total on all 120 pairs before
    /// being relied on: pairOI equals this sum to the last digit, so it IS the venue's definition
    /// of a pair's open interest and not an arithmetic of ours.</summary>
    private static double? Sum(AvSides? sides) =>
        sides is null || (sides.Long is null && sides.Short is null)
            ? null
            : (sides.Long ?? 0) + (sides.Short ?? 0);
}

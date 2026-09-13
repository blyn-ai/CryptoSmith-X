using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Gmx;

/// <summary>
/// GMX v2 perpetuals on Arbitrum as an <see cref="IExchangeMarketData"/> — oracle-priced positions against
/// a pool, the same market model as Avantis and priced the same way: from the venue's own impact function at
/// a stated size.
///
/// <b>One market per POOL, not per asset.</b> "BTC/USD [BTC-USDC]", "BTC/USD [BTC]" and "BTC/USD [TBTC]" are
/// three markets with their own open interest, funding, capacity and impact — a trader on one does not move
/// the others. Each is an instrument, and all three sit on the BTC page.
///
/// <b>What comes from where.</b>
///
///   /markets/tickers (one call)   oracle min/max and mark, OI in tokens and USD, capacity per side, funding
///   /markets/info (one call)      the impact function's parameters — so bid, ask and depth cost no request
///                                 per probe: <see cref="GmxImpact"/> evaluates the SDK's own function
///   /pairs (one call)             24h volume in base and USD — checked against the tape, see 0063
///   /trades/search                every account's executions: the tape, and the liquidations (orderType 7)
///   /prices/ohlcv                 the ORACLE's 1-minute bars
///
/// <b>Funding is continuous.</b> The ticker's rates are per hour (fundingFactorPerSecond × 3 600, measured)
/// and accrue every second; there is no settlement instant, so no next-funding time is written, and no
/// funding history either — there is no discrete payment to have a history of.
/// </summary>
public sealed class GmxPerpMarketData : IExchangeMarketData
{
    /// <summary>The size every quote is taken at, and the size the badge names. The same $10,000 Avantis is
    /// quoted at, so the two oracle venues' spreads are read at one size.</summary>
    public const double QuotedNotionalUsd = 10_000;

    private const double Precision = 1e30;
    private const int TradePage = 100;
    private const int MaxTapePages = 5;
    private const int ColdLookupsPerPass = 24;
    private const int MaxLiquidationPages = 40;

    private static readonly TimeSpan FrameMaxAge = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WalkMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ColdRetry = TimeSpan.FromMinutes(10);
    private static readonly double[] Thresholds = [10d, 25d, 50d];

    private readonly GmxClient _client;
    private readonly ConcurrentDictionary<string, MarketRef> _markets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _symbolByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _coldTried = new(StringComparer.Ordinal);
    private readonly RestTape _tape = new();
    private readonly SemaphoreSlim _frameLock = new(1, 1);
    private readonly SemaphoreSlim _walkLock = new(1, 1);
    private readonly SemaphoreSlim _tapeLock = new(1, 1);
    private Frame? _frame;
    private LiquidationWalk? _walk;
    private long? _tapeNewest;

    public GmxPerpMarketData(GmxClient client) => _client = client;

    public string SegmentCode => "gmx-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        // The oracle's bars. No traded-candle history exists on this venue's API.
        new("candles_index", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("liquidations", "rest"),
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var markets = await _client.GetMarketsAsync(ct);
        var decimals = (await _client.GetTokensAsync(ct))
            .GroupBy(t => t.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var disabled = (await FrameAsync(ct)).Info.Where(i => i.IsDisabled).Select(i => i.MarketTokenAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new List<Instrument>(markets.Count);
        foreach (var m in markets)
        {
            var slash = m.Symbol.IndexOf('/', StringComparison.Ordinal);
            if (m.IsSpotOnly || slash <= 0 || !decimals.TryGetValue(m.IndexTokenAddress, out var index))
            {
                // A swap-only pool has no position to price.
                continue;
            }

            _markets[m.Symbol] = new MarketRef(m.MarketTokenAddress, index.Symbol, index.Decimals, m.IsListed);
            _symbolByAddress[m.MarketTokenAddress] = m.Symbol;

            list.Add(new Instrument(
                ExchangeSymbol: m.Symbol,
                // "BTC/USD [BTC-USDC]": the asset, the quote, and the pool's collateral in brackets.
                BaseAssetRaw: m.Symbol[..slash],
                QuoteAssetRaw: "USD",
                ContractMultiplier: 1m,
                PriceStep: null,
                QtyStep: null,
                MinQty: null,
                // A USD size at 1e30 — one dollar on BTC [BTC-USDC].
                MinNotional: Usd(m.MinPositionSizeUsd) is { } min && min > 0 ? (decimal)min : null,
                // The rate is stated per hour and accrues per second; the hour is the unit it is published in.
                FundingIntervalHours: 1,
                ListedAt: null,
                Status: disabled.Contains(m.MarketTokenAddress) ? InstrumentStatus.Halted : InstrumentStatus.Trading,
                RawJson: JsonSerializer.Serialize(m)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        await EnsureCatalogAsync(ct);
        var frame = await FrameAsync(ct, fresh: true);
        var pairs = (await _client.GetPairsAsync(ct))
            .GroupBy(p => p.PoolId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        await PollTapeAsync(ct);
        await ColdLastAsync(ct);

        var list = new List<Ticker>(frame.Tickers.Count);
        foreach (var t in frame.Tickers)
        {
            if (!_markets.TryGetValue(t.Symbol, out var market))
            {
                continue;
            }

            var impact = Impact(frame, t, market);
            var mid = impact?.Mid;
            var pair = pairs.GetValueOrDefault(t.MarketTokenAddress);
            var last = _tape.Last(t.Symbol);
            var tokens = Math.Pow(10, market.Decimals);

            list.Add(new Ticker(
                ExchangeSymbol: t.Symbol,
                ReceivedAt: frame.At,
                LastPrice: last?.Price,
                LastTradeAt: last?.At,
                // Executable at QuotedNotionalUsd, from the venue's impact function — not a resting order.
                BidPrice: impact?.Bid(QuotedNotionalUsd),
                AskPrice: impact?.Ask(QuotedNotionalUsd),
                // HOW MUCH MORE THE POOL WILL TAKE per side — the venue's own availableLiquidity, in base
                // units at the mid this same frame carries.
                BidSize: Base(Usd(t.CapacityShort?.AvailableLiquidity), mid),
                AskSize: Base(Usd(t.CapacityLong?.AvailableLiquidity), mid),
                MarkPrice: Usd(t.MarkPrice),
                // The oracle, which is what this venue prices against: the middle of its min/max.
                IndexPrice: mid,
                // What a LONG pays per hour, the column's meaning everywhere. The venue signs the paying side
                // negative (SDK getFundingFactorPerPeriod), hence the flip. The short side is not its negative
                // — the receiving side's rate is scaled by the ratio of the two sides' interest.
                FundingRate: Usd(t.FundingRateLong) is { } fl ? -fl : null,
                Turnover24h: pair?.TargetVolume,
                Volume24hBase: pair?.BaseVolume,
                OpenInterest: Sum(Num(t.LongInterestInTokens), Num(t.ShortInterestInTokens)) / tokens,
                OpenInterestAt: frame.At,
                OiQuote: Sum(Usd(t.LongInterestUsd), Usd(t.ShortInterestUsd)),
                Depth: null));
        }

        return list;
    }

    /// <summary>
    /// The impact curve, read as a book: how much the pool will take on each side within 10, 25 and 50 bps
    /// of the oracle mid, and what the largest order it will take costs. No request per probe — the function
    /// and its inputs are one frame, shared with the snapshot pass.
    /// </summary>
    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        await EnsureCatalogAsync(ct);
        if (!_markets.TryGetValue(exchangeSymbol, out var market))
        {
            return null;
        }

        var frame = await FrameAsync(ct);
        var ticker = frame.Tickers.FirstOrDefault(t => t.Symbol == exchangeSymbol);
        if (ticker is null || Impact(frame, ticker, market) is not { } impact)
        {
            return null;
        }

        var capBid = Usd(ticker.CapacityShort?.AvailableLiquidity) ?? 0;
        var capAsk = Usd(ticker.CapacityLong?.AvailableLiquidity) ?? 0;
        var bid = Thresholds.Select(b => impact.SizeWithin(b, isLong: false, capBid)).ToArray();
        var ask = Thresholds.Select(b => impact.SizeWithin(b, isLong: true, capAsk)).ToArray();

        return new Depth(
            Bid10Bps: bid[0], Ask10Bps: ask[0],
            Bid25Bps: bid[1], Ask25Bps: ask[1],
            Bid50Bps: bid[2], Ask50Bps: ask[2],
            At: frame.At,
            Mid: impact.Mid,
            // The cost of the largest order the pool takes; 0 where it takes none — measured and empty.
            ReachBidBps: capBid > 0 ? impact.CostBps(capBid, isLong: false) : 0,
            ReachAskBps: capAsk > 0 ? impact.CostBps(capAsk, isLong: true) : 0);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    /// <summary>No traded bars: the API has none, and the page's traded candles come from venues that do.</summary>
    public Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Candle>>([]);

    /// <summary>No funding history: funding accrues per second, and there is no payment to list.</summary>
    public Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FundingRate>>([]);

    /// <summary>The oracle's bars for the pool's index token. Several pools share one token, so they share
    /// the series; the route answers only its latest thousand minutes, whatever is asked.</summary>
    public async Task<IReadOnlyList<PriceCandle>> GetPriceCandles1mAsync(
        string exchangeSymbol, string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await EnsureCatalogAsync(ct);
        if (series != "index" || !_markets.TryGetValue(exchangeSymbol, out var market))
        {
            return [];
        }

        var limit = (int)Math.Clamp((DateTimeOffset.UtcNow - from).TotalMinutes + 2, 1, 1000);
        var bars = await _client.GetOracleCandlesAsync(market.IndexSymbol, limit, ct);

        var list = new List<PriceCandle>(bars.Count);
        foreach (var b in bars.OrderBy(b => b.Timestamp))
        {
            var open = DateTimeOffset.FromUnixTimeMilliseconds(b.Timestamp);
            if (open < from || open + TimeSpan.FromMinutes(1) > to
                || Dec(b.Open) is not { } o || Dec(b.High) is not { } h || Dec(b.Low) is not { } l || Dec(b.Close) is not { } c)
            {
                continue;
            }

            list.Add(new PriceCandle(exchangeSymbol, series, open, o, h, l, c));
        }

        return list;
    }

    /// <summary>
    /// Liquidated size per hour in base units, from the venue's own executions of orderType 7.
    ///
    /// One walk serves every market: the search is venue-wide either way, and asking it once per pool would
    /// read the same pages a hundred times. Hours are reported only where the walk reached, and an hour it
    /// covered with nothing in it is a counted zero.
    /// </summary>
    public async Task<IReadOnlyList<LiquidationBucket>> GetLiquidationVolumeAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await EnsureCatalogAsync(ct);
        if (!_markets.TryGetValue(exchangeSymbol, out var market))
        {
            return [];
        }

        var walk = await Walk(from, ct);
        var sums = new Dictionary<DateTimeOffset, double>();
        foreach (var t in walk.ByMarket.GetValueOrDefault(market.Address) ?? [])
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp);
            if (at < from || at > to || Num(t.SizeDeltaInTokens) is not { } raw)
            {
                continue;
            }

            var hour = FloorHour(at);
            sums[hour] = sums.GetValueOrDefault(hour) + raw / Math.Pow(10, market.Decimals);
        }

        var firstWhole = walk.Exhausted ? FloorHour(from) : FloorHour(walk.Reached) + TimeSpan.FromHours(1);
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

    private async Task<LiquidationWalk> Walk(DateTimeOffset from, CancellationToken ct)
    {
        await _walkLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_walk is { } cached && cached.From <= from && now - cached.At < WalkMaxAge)
            {
                return cached;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var byMarket = new Dictionary<string, List<GmxTrade>>(StringComparer.OrdinalIgnoreCase);
            var reached = now;
            var exhausted = false;
            string? cursor = null;

            for (var page = 0; page < MaxLiquidationPages; page++)
            {
                var body = await _client.SearchTradesAsync([GmxClient.Liquidation], null, from, TradePage, cursor, ct);
                foreach (var t in body.Trades ?? [])
                {
                    if (!seen.Add(t.Id))
                    {
                        // An offset cursor over a list that grows at the top re-serves its boundary.
                        continue;
                    }

                    var at = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp);
                    if (at < reached)
                    {
                        reached = at;
                    }

                    if (!byMarket.TryGetValue(t.MarketAddress, out var rows))
                    {
                        byMarket[t.MarketAddress] = rows = [];
                    }

                    rows.Add(t);
                }

                if (!body.HasMore || body.NextCursor is null)
                {
                    // fromTimestamp bounded the search, so the end of it is the start of the window.
                    exhausted = true;
                    break;
                }

                cursor = body.NextCursor;
            }

            return (_walk = new LiquidationWalk(from, now, reached, exhausted, byMarket));
        }
        finally
        {
            _walkLock.Release();
        }
    }

    /// <summary>The venue-wide tape since the last poll, oldest first into <see cref="RestTape"/> so the
    /// newest of several same-second prints is the one left as Last.</summary>
    private async Task PollTapeAsync(CancellationToken ct)
    {
        await _tapeLock.WaitAsync(ct);
        try
        {
            var collected = new List<GmxTrade>();
            DateTimeOffset? since = _tapeNewest is { } n ? DateTimeOffset.FromUnixTimeSeconds(n - 120) : null;
            string? cursor = null;
            for (var page = 0; page < MaxTapePages; page++)
            {
                var body = await _client.SearchTradesAsync(GmxClient.PositionOrderTypes, null, since, TradePage, cursor, ct);
                var trades = body.Trades ?? [];
                collected.AddRange(trades);
                if (!body.HasMore || body.NextCursor is null || since is null || trades.Count == 0)
                {
                    break;
                }

                cursor = body.NextCursor;
            }

            lock (_tape)
            {
                Fold(collected);
            }
        }
        finally
        {
            _tapeLock.Release();
        }
    }

    /// <summary>
    /// The last execution of a market the venue-wide page did not reach — a quiet pool's last trade can be
    /// days old, and still is its last trade. Bounded per pass, and a market that answers nothing is not
    /// asked again for a while.
    /// </summary>
    private async Task ColdLastAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var due = _markets
            .Where(m => _tape.Last(m.Key) is null
                        && (!_coldTried.TryGetValue(m.Key, out var tried) || now - tried > ColdRetry))
            .OrderByDescending(m => m.Value.IsListed)
            .Take(ColdLookupsPerPass)
            .ToList();

        // Four at a time: sequential, a cold start's 24 lookups held the snapshot pass for 12 s (measured).
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (entry, token) =>
        {
            _coldTried[entry.Key] = now;
            GmxTradesPage body;
            try
            {
                body = await _client.SearchTradesAsync(GmxClient.PositionOrderTypes, entry.Value.Address, null, 1, null, token);
            }
            catch (HttpRequestException ex) when (ex is not VenueRateLimitedException)
            {
                // The venue's own search gives up on a market with nothing recent: ~10.5 s, then HTTP 500 —
                // 6 of 122 markets from the test host (LINK [WETH-USDC], QQQ, SPY …), measured 2026-09-13.
                // That pool's Last trade stays a dash until a later pass or the venue-wide page reaches it;
                // it must not fail every other pool's snapshot, which is what it did on the first passes.
                return;
            }

            lock (_tape)
            {
                Fold(body.Trades ?? []);
            }
        });
    }

    private void Fold(IEnumerable<GmxTrade> trades)
    {
        foreach (var group in trades.GroupBy(t => t.MarketAddress, StringComparer.OrdinalIgnoreCase))
        {
            if (!_symbolByAddress.TryGetValue(group.Key, out var symbol) || !_markets.TryGetValue(symbol, out var market))
            {
                continue;
            }

            var events = new List<TradeEvent>();
            foreach (var t in group.OrderBy(t => t.Timestamp))
            {
                if (ToEvent(t, symbol, market.Decimals) is not { } e)
                {
                    continue;
                }

                events.Add(e);
                _tape.Saw(symbol, e.Price, e.EventTime);

                if (_tapeNewest is null || t.Timestamp > _tapeNewest)
                {
                    _tapeNewest = t.Timestamp;
                }
            }

            _tape.Observe(symbol, events);
        }
    }

    /// <summary>One execution as a print. Trade prices are per RAW token unit at 1e30, so a whole coin's price
    /// needs the index token's decimals back: 7.7057e26 × 10^8 / 1e30 is 77 057 on BTC.</summary>
    internal static TradeEvent? ToEvent(GmxTrade t, string symbol, int decimals)
    {
        if (t.EventName != "OrderExecuted" || Num(t.ExecutionPrice) is not { } rawPrice || Num(t.SizeDeltaInTokens) is not { } rawSize)
        {
            return null;
        }

        var scale = Math.Pow(10, decimals);
        var price = rawPrice * scale / Precision;
        var qty = rawSize / scale;
        return price > 0 && qty > 0
            ? new TradeEvent(symbol, DateTimeOffset.FromUnixTimeSeconds(t.Timestamp), t.Id, null, price, qty, TakerSide(t), TradeType(t.OrderType))
            : null;
    }

    /// <summary>Opening a long and closing a short both buy from the pool; the other two sell.</summary>
    internal static string TakerSide(GmxTrade t)
    {
        var increase = t.OrderType is 2 or 3 or 8;
        return increase == t.IsLong ? "buy" : "sell";
    }

    /// <summary>The venue marks a liquidation by its order type; everything else it executed is a fill.</summary>
    internal static string TradeType(int orderType) => orderType == GmxClient.Liquidation ? "liquidation" : "fill";

    private async Task EnsureCatalogAsync(CancellationToken ct)
    {
        if (_markets.IsEmpty)
        {
            await GetInstrumentsAsync(ct);
        }
    }

    private async Task<Frame> FrameAsync(CancellationToken ct, bool fresh = false)
    {
        await _frameLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_frame is { } f && now - f.At < (fresh ? TimeSpan.FromSeconds(5) : FrameMaxAge))
            {
                return f;
            }

            // Both halves of one frame, fetched together so a quote never mixes an old imbalance with a new price.
            var tickers = _client.GetTickersAsync(ct);
            var info = _client.GetMarketsInfoAsync(ct);
            await Task.WhenAll(tickers, info);
            return (_frame = new Frame(DateTimeOffset.UtcNow, tickers.Result, info.Result,
                info.Result.GroupBy(i => i.MarketTokenAddress, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)));
        }
        finally
        {
            _frameLock.Release();
        }
    }

    /// <summary>The impact function for one pool at this frame, or null when the frame lacks a price.</summary>
    internal static GmxImpact? Impact(Frame frame, GmxTicker t, MarketRef market)
    {
        if (Usd(t.MinPrice) is not { } min || Usd(t.MaxPrice) is not { } max || min <= 0 || max <= 0
            || !frame.InfoByAddress.TryGetValue(t.MarketTokenAddress, out var i))
        {
            return null;
        }

        var mid = (min + max) / 2;
        var tokens = Math.Pow(10, market.Decimals);
        var byTokens = i.UseOpenInterestInTokensForBalance;

        // getOpenInterestForBalance: tokens at mid where the market balances by tokens, USD otherwise.
        var longOi = byTokens ? (Num(i.LongInterestInTokens) ?? 0) / tokens * mid : Usd(i.LongInterestUsd) ?? 0;
        var shortOi = byTokens ? (Num(i.ShortInterestInTokens) ?? 0) / tokens * mid : Usd(i.ShortInterestUsd) ?? 0;

        // getVirtualInventoryForPositionImpact: configured only where the virtual token id is not the zero hash.
        var virtualId = i.VirtualIndexTokenId;
        bool? configured = virtualId is null ? null : virtualId.Trim('0', 'x').Length > 0;
        var virtualInventory = byTokens
            ? (Num(i.VirtualInventoryForPositionsInTokens) ?? 0) / tokens * mid
            : Usd(i.VirtualInventoryForPositions) ?? 0;

        return new GmxImpact(
            MinPrice: min,
            MaxPrice: max,
            LongOiUsd: longOi,
            ShortOiUsd: shortOi,
            TokensForBalance: byTokens,
            FactorPositive: Usd(i.PositionImpactFactorPositive) ?? 0,
            FactorNegative: Usd(i.PositionImpactFactorNegative) ?? 0,
            ExponentPositive: Usd(i.PositionImpactExponentFactorPositive) ?? 1,
            ExponentNegative: Usd(i.PositionImpactExponentFactorNegative) ?? 1,
            MaxFactorPositive: Usd(i.MaxPositionImpactFactorPositive) ?? 0,
            MaxFactorNegative: Usd(i.MaxPositionImpactFactorNegative) ?? 0,
            HasVirtualInventory: configured ?? virtualInventory != 0,
            VirtualInventoryUsd: virtualInventory);
    }

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>A 1e30 fixed-point figure — USD, a price per whole token, or a factor.</summary>
    private static double? Usd(string? s) => Num(s) is { } v ? v / Precision : null;

    private static double? Dec(string? s) => Num(s);

    private static double? Sum(double? a, double? b) => a is { } x && b is { } y ? x + y : null;

    private static double? Base(double? usd, double? price) => usd is { } u && price is { } p && p > 0 ? u / p : null;

    private static DateTimeOffset FloorHour(DateTimeOffset at) =>
        DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / 3600 * 3600);

    internal sealed record MarketRef(string Address, string IndexSymbol, int Decimals, bool IsListed);

    internal sealed record Frame(
        DateTimeOffset At,
        IReadOnlyList<GmxTicker> Tickers,
        IReadOnlyList<GmxMarketInfo> Info,
        IReadOnlyDictionary<string, GmxMarketInfo> InfoByAddress);

    private sealed record LiquidationWalk(
        DateTimeOffset From,
        DateTimeOffset At,
        DateTimeOffset Reached,
        bool Exhausted,
        IReadOnlyDictionary<string, List<GmxTrade>> ByMarket);
}

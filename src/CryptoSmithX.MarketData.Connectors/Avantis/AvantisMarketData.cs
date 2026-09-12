using System.Collections.Concurrent;
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
/// <b>Where the price comes from, and where it does not.</b> The catalogue carries none: checked
/// live on 2026-09-11, and the venue's own Socket.IO broadcast too — 86 KB of <c>RES:DATA</c>
/// carrying pairInfos, with no occurrence of price, index, mid or last anywhere in it. There IS a
/// batched endpoint named for the job, <c>/v1/price-feeds/last-price</c>, and it is a trap: its
/// rows carried timestamps one and two DAYS old, and a BTC price 560 dollars off the live one. The
/// venue's own SDK reads exactly that endpoint for <c>markets.price()</c>, so this is their bug
/// inherited rather than ours invented — and it is why nothing here calls it.
///
/// What is fresh is the candle shim, to the minute. Its newest closed bar fills
/// <c>index_price</c> — the oracle's reference, which is what that column is documented to be —
/// and the bar's OWN instant travels with it in <c>venue_ts</c>, because a close is as old as its
/// bar and the row's <c>received_at</c> is not. <c>last_price</c> and <c>mark_price</c> stay empty:
/// this venue publishes no trade and marks against the unadjusted oracle without stating a figure,
/// and the index under another name would be neither.
/// </summary>
public sealed class AvantisMarketData : IExchangeMarketData
{
    private readonly AvantisClient _client;
    private readonly IAvantisCatalogFeed? _ws;

    /// <summary>
    /// The last closed oracle bar this adapter has seen, per symbol — filled by the candle pass and
    /// read by the ticker pass.
    ///
    /// <b>It costs nothing.</b> The candles_index collector already fetches these bars on its own
    /// cadence; remembering the newest close is free, where asking the venue again from the ticker
    /// path would be one request per symbol per pass for a figure already in hand.
    ///
    /// <b>And it carries its own clock.</b> A bar's close is as old as the bar — up to a minute —
    /// while the snapshot's received_at is the instant the row was written. Those are different
    /// facts, so the bar's own time travels with it and is written to venue_ts, which exists for
    /// exactly this: the venue's clock for a figure, as distinct from ours for the row.
    /// </summary>
    private readonly ConcurrentDictionary<string, (double Close, DateTimeOffset At)> _lastBar = new(StringComparer.Ordinal);

    /// <summary>The venue's own tape, kept as a rolling day. See <see cref="AvantisTape"/> for why
    /// the window is accumulated rather than fetched.</summary>
    private readonly AvantisTape _tape = new();

    /// <summary>Trades seen on the tape but not yet handed to the collector. A REST poll filling a
    /// buffer a push stream would fill is the same contract from the collector's side, and it is
    /// what lets the shared TradeCollector persist this venue without knowing any of the
    /// above.</summary>
    private readonly ConcurrentQueue<TradeEvent> _pendingTrades = new();

    /// <summary>How many quote requests may be in flight at once. Measured: the engine sustained
    /// 53.6 req/s at this concurrency with no throttling and an identical status split to
    /// concurrency 1, so this is politeness rather than a limit we found.</summary>
    private const int QuoteConcurrency = 8;

    public AvantisMarketData(AvantisClient client, IAvantisCatalogFeed? ws = null)
    {
        _client = client;
        _ws = ws;
    }

    public string SegmentCode => "avantis-perp";

    /// <summary>
    /// Only what this venue honestly serves — and four of these were declared absent on a mistake
    /// that took a full audit to find.
    ///
    /// <b>What the mistake was.</b> The first pass read <c>/v2/trading</c>, saw no book and no tape
    /// in it, and generalised from one endpoint to a venue. <c>depth</c>, <c>trades</c> and
    /// <c>liquidations</c> were declared impossible on that basis. They are not: the risk engine
    /// quotes a cost for a size and a side, and <c>/v1/history/recent-trades</c> is a public,
    /// market-wide tape. Both were found by reading the SDK rather than guessing at it, and both
    /// answer anonymously — measured on mainnet.
    ///
    /// <b>depth is a QUOTE curve, not resting size.</b> The collector and the column are shared with
    /// book venues, and the figure is comparable — cumulative notional executable within a band —
    /// but nobody is standing there offering it. That distinction lives in the column's badge and in
    /// <see cref="AvantisQuotes"/>'s own remarks, and it must not be lost just because the numbers
    /// now line up.
    ///
    /// <c>snapshot</c> is "rest,ws": the catalogue arrives either way, and the socket is the venue's
    /// own rather than the oracle's — verified by connecting to it.
    /// </summary>
    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest,ws"),
        new("candles_index", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("liquidations", "rest"),
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

            var symbol = Symbol(p);
            _pairIndex[symbol] = p.Index;
            var bar = _lastBar.TryGetValue(symbol, out var seen) ? seen : ((double Close, DateTimeOffset At)?)null;

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
                // Marking is against the oracle unadjusted, which the venue does not publish as a
                // figure of its own — so this stays absent rather than being the index under
                // another name.
                MarkPrice: null,
                // THE ORACLE'S PRICE, which is what index_price is documented to be: a reference
                // from outside this venue, written as published. Taken from the newest closed bar
                // the candle pass already fetched (see _lastBar) — never from
                // /v1/price-feeds/last-price, which carries the right name and a price one to two
                // days old, measured.
                IndexPrice: bar?.Close,
                // No discrete payment: the carry is continuous and its components are a different
                // quantity. Writing a carry rate here would misstate the unit as well as the thing.
                FundingRate: null,
                Turnover24h: null,
                // Both units published, so both are written and neither is derived.
                OpenInterest: Sum(p.CoinOi),
                OpenInterestAt: now,
                Depth: null,
                OiQuote: p.PairOi ?? Sum(p.OpenInterest),
                MarketOpen: p.Feed?.Attributes?.IsOpen)
            {
                // The bar's own instant, not ours. Without it the page would show a price up to a
                // minute old under an age of two seconds — the one lie this whole surface exists
                // to prevent.
                VenueTs = bar?.At,
            });
        }

        // The catalogue is one response; these are one request per pair, so they are done together
        // and under a bound rather than in the loop above. Two quote calls and one tape read per
        // pair: 153 requests a minute across fifty-one listings, which at the measured 53.6 req/s
        // is about three seconds of the pass.
        await EnrichAsync(list, now, ct);
        return list;
    }

    /// <summary>
    /// Fills the columns that come from outside the catalogue: the executable quote, and the tape.
    ///
    /// <b>Why these are not in the catalogue loop.</b> Each costs a round trip, and the loop above
    /// costs none — it walks a response already in hand. Keeping them apart is what makes the
    /// cost of this pass legible instead of hidden in an iteration.
    /// </summary>
    private async Task EnrichAsync(List<Ticker> list, DateTimeOffset now, CancellationToken ct)
    {
        var quotes = new AvantisQuotes(_client);
        using var gate = new SemaphoreSlim(QuoteConcurrency);

        // Materialised, and writing into an array rather than back into `list`. Select is lazy, so
        // a task that mutated `list` while WhenAll was still pulling tasks out of that same Select
        // threw "Collection was modified" — caught by the adapter's own tests before it ever ran
        // against a venue.
        var filled = new Ticker[list.Count];
        var work = list.Select(async (ticker, i) =>
        {
            filled[i] = ticker;
            if (!_pairIndex.TryGetValue(ticker.ExchangeSymbol, out var pairIndex))
            {
                return;
            }

            await gate.WaitAsync(ct);
            try
            {
                // The tape first: it is one request and it never fails the way a quote can, so a
                // venue that stops quoting still prints a last trade.
                try
                {
                    var trades = await _client.GetRecentTradesAsync(pairIndex, ct);
                    foreach (var fresh in _tape.Observe(ticker.ExchangeSymbol, trades, now))
                    {
                        _pendingTrades.Enqueue(fresh);
                    }
                }
                catch (HttpRequestException)
                {
                    // One pair's tape being unreachable is not the pass failing. The window keeps
                    // what it had, and the age on the figure is what tells the reader.
                }

                AvantisQuote quote = AvantisQuote.None;
                if (ticker.IndexPrice is { } index)
                {
                    try
                    {
                        quote = await quotes.BaselineAsync(pairIndex, index, multiplier: 1m, ct);
                    }
                    catch (HttpRequestException)
                    {
                    }
                }

                var last = _tape.Last(ticker.ExchangeSymbol);
                filled[i] = ticker with
                {
                    // Executable, at a stated size — not a resting order. The size travels with the
                    // figure (AvantisQuote.QuotedNotional) and the column's badge says so.
                    BidPrice = quote.Bid,
                    AskPrice = quote.Ask,
                    // The venue's own last print, market-wide. Public and anonymous, which an
                    // earlier reading of this venue concluded did not exist.
                    LastPrice = last?.Price,
                    LastTradeAt = last?.At,
                    Turnover24h = _tape.Turnover(ticker.ExchangeSymbol, now),
                };
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(work.ToList());

        for (var i = 0; i < list.Count; i++)
        {
            list[i] = filled[i];
        }
    }

    /// <summary>Everything the tape turned up since the last pass. A REST poll on the producing
    /// side, the same contract on the consuming one.</summary>
    public IReadOnlyList<TradeEvent> DrainTrades()
    {
        var drained = new List<TradeEvent>();
        while (_pendingTrades.TryDequeue(out var trade))
        {
            drained.Add(trade);
        }

        return drained;
    }

    /// <summary>
    /// Bucketed liquidation volume, from the same tape the trades came off.
    ///
    /// NOT a second drain. This venue marks a liquidation inline on its own tape — exactly the
    /// shape Kraken has — so the events are already in <see cref="DrainTrades"/> with
    /// <c>trade_type = 'liquidation'</c>, and draining them again here would count the same
    /// executed notional twice. That double count is the precise hazard the interface's own remarks
    /// warn about, and this is the side of the fork that avoids it.
    /// </summary>
    public Task<IReadOnlyList<LiquidationBucket>> GetLiquidationVolumeAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult(_tape.LiquidationBuckets(
            exchangeSymbol, from, to, intervalSeconds: 3600, unit: "quote"));

    /// <summary>
    /// The venue's quote curve, read as a book.
    ///
    /// There are no resting orders to walk, so the same questions are asked directly: how much will
    /// you take at ten basis points, at twenty-five, at fifty, and where do you stop quoting
    /// altogether. The answers come back in the shape every other venue's book produces —
    /// cumulative notional in the quote asset per side — so the column compares like with like.
    ///
    /// Measured on four instruments spanning four orders of magnitude of liquidity (ETH $11.5M at
    /// 25 bps against PENGU's $61k), ~79 requests per symbol with the sweep's own cost cache. That
    /// is why this hangs off <c>DepthCollector</c>'s cadence rather than the minute pass.
    /// </summary>
    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        if (!Index(exchangeSymbol, out var pairIndex, out var multiplier)
            || !_lastBar.TryGetValue(exchangeSymbol, out var bar))
        {
            // No oracle price means no size to quote — see AvantisQuoteMath.CoinSize. Null is
            // "depth was not collected this frame", which 0030 keeps distinct from "measured and
            // found nothing".
            return null;
        }

        var (depth, _, _) = await new AvantisQuotes(_client)
            .CurveAsync(pairIndex, bar.Close, multiplier, DateTimeOffset.UtcNow, ct);
        return depth;
    }

    /// <summary>The pair's index in the venue's own catalogue — the handle every risk-engine call
    /// takes — and its contract multiplier, both from the catalogue already in hand.</summary>
    private bool Index(string exchangeSymbol, out int pairIndex, out decimal multiplier)
    {
        // From the ticker pass's own cache rather than from Fresh(): the socket is an accelerator,
        // not a dependency, and a depth sweep must not go quiet because the websocket happens to be
        // reconnecting. One unit of exposure is one base unit here, which is a fact about the venue
        // and not a default — see the discovery pass, which states it for the same reason.
        multiplier = 1m;
        return _pairIndex.TryGetValue(exchangeSymbol, out pairIndex);
    }

    /// <summary>Symbol to the venue's own pair index — the handle every risk-engine call takes.
    /// Filled by the ticker pass from the catalogue it reads anyway, for the same reason
    /// <see cref="_lastBar"/> is: the figure is already in hand, and fetching it again from the
    /// depth path would be a request per symbol for something we just read.</summary>
    private readonly ConcurrentDictionary<string, int> _pairIndex = new(StringComparer.Ordinal);

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

            // Newest wins: the shim returns bars oldest first, so the last one through here is the
            // freshest closed bar and is what the ticker pass will report as the index.
            _lastBar[exchangeSymbol] = (b.Close, open + TimeSpan.FromMinutes(1));
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

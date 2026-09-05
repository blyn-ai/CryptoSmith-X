using System.Globalization;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// WEEX Futures as an <see cref="IExchangeMarketData"/>. A dumb translator: no normalisation of its
/// own — the raw base is the venue's own <c>underlying_index</c> field, which matches the symbol
/// exactly (verified against all listed contracts; e.g. '1000FLOKI', not 'FLOKI') and the Hub's
/// asset_alias table resolves it. No retry/logging/sleeping on the REST calls: an error propagates
/// and the collector loop counts it.
///
/// WEEX's REST is thinner than Kraken's: the batched ticker call carries neither book size, funding
/// rate, nor open interest, so <see cref="GetTickersAsync"/> merges three more calls — two of them
/// batched (book size from the v3 clone API, funding from a batched funding-rate call) and one that
/// is genuinely per-symbol with no batch alternative (open interest), served from a background cache
/// (<see cref="WeexOpenInterestFeed"/>) rather than one blocking call per symbol per tick. A symbol
/// missing any of these — no market (price is 0), no book match, no funding match, no OI sample yet —
/// is simply omitted from the ticker batch; its snapshot row goes stale honestly rather than being
/// written with a fabricated value.
/// </summary>
public sealed class WeexFuturesMarketData : IExchangeMarketData
{
    private readonly WeexFuturesClient _client;
    private readonly IWeexOpenInterestFeed _openInterest;

    public WeexFuturesMarketData(WeexFuturesClient client, IWeexOpenInterestFeed openInterest)
    {
        _client = client;
        _openInterest = openInterest;
    }

    public string ExchangeCode => "weex-futures";

    // REST-only in V1 (see the commit that added this adapter for why WS was deferred).
    public IReadOnlyList<CollectionCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("depth", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var contracts = await _client.GetContractsAsync(ct);
        var funding = await _client.GetCurrentFundingRatesAsync(ct);
        var fundingBySymbol = funding.ToDictionary(f => f.Symbol, StringComparer.Ordinal);

        // /contracts lists a tail of symbols WEEX itself has abandoned. A price of 0 catches most of
        // them, but a handful (e.g. cmt_usdcusdt) still carry a stale non-zero last price with zero
        // 24h volume — found live, alongside the zero-price case: their /candles and /depth calls
        // 400/null rather than returning empty. Those two collectors have no per-symbol try/catch (a
        // venue is expected not to need one), so one such symbol would permanently wedge every pass at
        // the same point. Excluding a dead symbol from discovery is what keeps it out of their target
        // lists; it reappears the moment WEEX's own ticker shows real volume again, since discovery
        // re-derives this set every pass.
        var tickers = await _client.GetTickersAsync(ct);
        var live = tickers.Where(IsLive).Select(t => t.Symbol).ToHashSet(StringComparer.Ordinal);

        var list = new List<Instrument>(contracts.Count);
        foreach (var c in contracts)
        {
            // A contract WEEX still lists but whose ticker shows no price and no volume is reported
            // as Halted, not dropped. Dropping it made discovery lose sight of the symbol, and three
            // passes later the delisting sweep wrote 'delisted' — a lifecycle event we invented from
            // an absence of trades on a venue that never said any such thing. Halted is what we can
            // actually see: listed, not trading. The collectors still skip it because collect is
            // driven by status, so the reason for excluding it survives.
            var status = live.Contains(c.Symbol) ? InstrumentStatus.Trading : InstrumentStatus.Halted;

            // Funding interval varies per symbol (60/240/480 min observed) — unlike Kraken there is
            // no single constant, so it comes from the same batched call GetTickersAsync also merges.
            var intervalHours = fundingBySymbol.TryGetValue(c.Symbol, out var f) && f.CollectCycle > 0
                ? (short)Math.Max(1, f.CollectCycle / 60)
                : (short)8;   // WEEX's most common cycle, used only if the funding call missed this symbol

            list.Add(new Instrument(
                ExchangeSymbol: c.Symbol,
                BaseAssetRaw: c.UnderlyingIndex,
                QuoteAssetRaw: c.QuoteCurrency,
                // Sizes on WEEX are already expressed in base-asset units (minOrderSize matches the
                // venue's own contract_val exactly for every listed contract), so there is no
                // per-contract scaling to fold in here.
                ContractMultiplier: 1m,
                PriceStep: Step(c.TickSize),
                QtyStep: Step(c.SizeIncrement),
                MinQty: decimal.Parse(c.MinOrderSize, CultureInfo.InvariantCulture),
                // WEEX defines no minimum notional.
                MinNotional: null,
                FundingIntervalHours: intervalHours,
                // No listing-date field on /contracts.
                ListedAt: null,
                // WEEX gives no halt/delist signal on this endpoint; a contract with no live market
                // (price 0) is caught in GetTickersAsync instead — its snapshot simply never gets a
                // fresh row rather than this call inventing a status the venue does not report.
                Status: status,
                RawJson: RawJson(c)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var tickers = await _client.GetTickersAsync(ct);
        var bookTickers = await _client.GetBookTickersAsync(ct);
        var funding = await _client.GetCurrentFundingRatesAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var bookBySymbol = bookTickers.ToDictionary(b => b.Symbol, StringComparer.Ordinal);
        var fundingBySymbol = funding.ToDictionary(f => f.Symbol, StringComparer.Ordinal);

        var list = new List<Ticker>(tickers.Count);
        foreach (var t in tickers)
        {
            if (!IsLive(t))
            {
                continue;   // no real market — see the note on this same check in GetInstrumentsAsync
            }

            var last = Parse(t.Last);

            if (!bookBySymbol.TryGetValue(ToV3Symbol(t.Symbol), out var book))
            {
                continue;   // no batched size sample for this symbol this round
            }

            if (!fundingBySymbol.TryGetValue(t.Symbol, out var fund))
            {
                continue;   // no funding sample this round
            }

            if (!_openInterest.TryGet(t.Symbol, out var oi, out var oiAt))
            {
                continue;   // the background OI cycle has not reached this symbol yet
            }

            var mark = Parse(t.MarkPrice);
            list.Add(new Ticker(
                ExchangeSymbol: t.Symbol,
                ReceivedAt: now,
                LastPrice: last,
                BidPrice: Parse(t.BestBid),
                AskPrice: Parse(t.BestAsk),
                BidSize: Parse(book.BidQty),
                AskSize: Parse(book.AskQty),
                MarkPrice: mark,
                IndexPrice: Parse(t.IndexPrice),
                // Already a fraction of notional per interval — WEEX does not report an absolute rate
                // on any REST call, unlike Kraken's ticker.
                FundingRate: Parse(fund.FundingRate),
                Turnover24h: Parse(t.Volume24h),
                OpenInterest: oi,
                // Its own, later time — OI is a separate, slower call (0001's own words), served here
                // from the background cache rather than this request.
                OpenInterestAt: oiAt,
                // The book is a separate per-symbol call; see GetOrderBookAsync and DepthCollector.
                Depth: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // WEEX has no time-range params on this endpoint, only a row limit; ask for enough to cover
        // the window (capped at the venue's own maximum) and let the caller's [from,to] do the rest.
        var minutes = Math.Clamp((int)Math.Ceiling((to - from).TotalMinutes) + 1, 1, 1000);
        var rows = await _client.GetCandles1mAsync(exchangeSymbol, minutes, ct);

        var list = new List<Candle>(rows.Count);
        foreach (var r in rows)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(r[0], CultureInfo.InvariantCulture));
            if (openTime < from || openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;   // outside the requested window, or the bar still forming
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Parse(r[1]),
                High: Parse(r[2]),
                Low: Parse(r[3]),
                Close: Parse(r[4]),
                // Index 5 is base-asset volume; index 6 (quote value) is not what the schema wants.
                Volume: Parse(r[5]),
                // WEEX klines carry no trade counter.
                TradeCount: null));
        }

        // The venue does not guarantee chronological order in the response.
        list.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetFundingHistoryAsync(exchangeSymbol, ct);

        // Newest first, capped at 100 by the venue with no pagination — a symbol funding hourly
        // (100 h ≈ 4 days) cannot fully backfill a 7-day window in one call; the collector's own
        // incremental catch-up (from = latest stored, not the floor) covers the rest over later passes.
        var list = new List<FundingRate>(rows.Count);
        foreach (var r in rows)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.FundingTime);
            if (at < from || at > to)
            {
                continue;
            }

            list.Add(new FundingRate(ExchangeSymbol: exchangeSymbol, FundingTime: at, Rate: Parse(r.FundingRate)));
        }

        list.Sort((a, b) => a.FundingTime.CompareTo(b.FundingTime));
        return list;
    }

    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var response = await _client.GetDepthAsync(exchangeSymbol, ct);
        // A symbol with no book (WEEX serves this as literal JSON nulls, not empty arrays) overrides
        // the record's [] default, so this still needs a null guard even with discovery already
        // excluding no-market symbols — a book can legitimately empty out between passes.
        var bids = (response.Bids ?? []).ConvertAll(l => (Parse(l[0]), Parse(l[1])));
        var asks = (response.Asks ?? []).ConvertAll(l => (Parse(l[0]), Parse(l[1])));
        // WEEX's depth response carries no server timestamp of its own, unlike Kraken's.
        return DepthMath.Compute(bids, asks, DateTimeOffset.UtcNow);
    }

    /// <summary>'cmt_btcusdt' → 'BTCUSDT': the transform v3's Binance-format symbols use. Verified
    /// against a live snapshot: 1011 of 1023 v2 symbols match a v3 book-ticker entry this way; the
    /// rest are dead or exotic listings this adapter already treats as absent (see the recon note).</summary>
    private static string ToV3Symbol(string v2Symbol) =>
        v2Symbol.StartsWith("cmt_", StringComparison.Ordinal)
            ? v2Symbol[4..].ToUpperInvariant()
            : v2Symbol.ToUpperInvariant();

    /// <summary>tick_size / size_increment are DECIMAL PLACE COUNTS on WEEX, not raw step values —
    /// confirmed against live prices (BTC last=78235.5, tick_size=1; DOGE last=0.08284, tick_size=5).</summary>
    private static decimal Step(int decimalPlaces)
    {
        var step = 1m;
        for (var i = 0; i < decimalPlaces; i++)
        {
            step /= 10m;
        }

        return step;
    }

    /// <summary>A contract with real trades — not just a non-zero reference price. Found live:
    /// cmt_usdcusdt carries last=1.0006 with volume_24h=0, and its /candles call still 400s.</summary>
    private static bool IsLive(WeexTicker t) => Parse(t.Last) > 0 && Parse(t.Volume24h) > 0;

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static string RawJson(WeexContract c) =>
        string.Create(CultureInfo.InvariantCulture,
            $$"""{"symbol":"{{c.Symbol}}","underlying_index":"{{c.UnderlyingIndex}}","quote_currency":"{{c.QuoteCurrency}}","tick_size":{{c.TickSize}},"size_increment":{{c.SizeIncrement}},"minOrderSize":"{{c.MinOrderSize}}"}""");
}

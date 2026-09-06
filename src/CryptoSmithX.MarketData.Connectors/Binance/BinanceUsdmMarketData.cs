using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// Binance USDⓈ-M Futures as an <see cref="IExchangeMarketData"/>. A dumb translator, like its
/// siblings: it hands back the venue's own base/quote spellings ('1000PEPE', not 'PEPE') and lets the
/// Hub's <c>asset_alias</c> table resolve them. No retry, no logging of failures, no sleeping — an
/// error propagates and the collector loop counts it.
///
/// WHAT IS CHEAP HERE AND WHAT IS NOT. Binance meters a per-IP budget in WEIGHT, 2400 per minute, and
/// charges wildly different amounts per endpoint, so the usual instinct — count requests — misreads
/// this venue badly in both directions. The whole-venue snapshot is three batched calls costing 55
/// weight together (bookTicker 5 + premiumIndex 10 + ticker/24hr 40), which is a full picture of ~570
/// perpetuals several times a minute. Against that, TWO datasets have no batched form at all and are
/// the entire cost of this adapter: open interest (weight 1 per symbol, no batch — HTTP 400 without
/// one, verified live) and the order book. Open interest is served from a background cycle
/// (<see cref="BinanceOpenInterestFeed"/>) rather than one blocking call per symbol per tick. The
/// order book is WS-first with a REST fallback whenever a feed is wired (<see cref="BinanceWsFeed"/>).
///
/// A symbol missing from any one of the merged sources — no top of book this round, no mark price, no
/// 24 h row, no open-interest sample yet — is omitted from the ticker batch entirely. Its snapshot row
/// then ages honestly instead of being written with a fabricated field, which is the same rule WEEX's
/// adapter applies for the same reason.
/// </summary>
public sealed class BinanceUsdmMarketData : IExchangeMarketData
{
    private readonly BinanceUsdmClient _client;
    private readonly IBinanceOpenInterestFeed _openInterest;
    private readonly IBinanceLiveFeed? _ws;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>Contract types seen and skipped, so the warning below fires once per value rather than
    /// once per symbol per pass. Not a correctness device — purely noise control on a log line whose
    /// job is to be noticed exactly once.</summary>
    private readonly HashSet<string> _reportedContractTypes = new(StringComparer.Ordinal);

    public BinanceUsdmMarketData(
        BinanceUsdmClient client,
        IBinanceOpenInterestFeed openInterest,
        IBinanceLiveFeed? ws = null,
        TimeProvider? clock = null,
        ILogger? log = null)
    {
        _client = client;
        _openInterest = openInterest;
        _ws = ws;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public string SegmentCode => "binance-usdm";

    // Depth is WS-first with a REST fallback whenever a feed is wired (see the ctor); it is honest to
    // declare both regardless of whether _ws happens to be null right now, since that is a config
    // fact (ws_url set or not), not a per-request coin flip. Everything else says 'rest' because it
    // is REST — see IBinanceLiveFeed for why the snapshot deliberately stays there even though the
    // venue does stream it.
    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("depth", "rest,ws"),
        new("candles", "rest"),
        new("funding", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var symbols = await _client.GetSymbolsAsync(ct);

        // Weight 0, and it is the only source of the funding interval: exchangeInfo does not carry
        // one. It lists only symbols that deviate from the venue default, so absence here is a
        // statement (8 hours) rather than a missing answer — 455 of the 780 rows present when this
        // was captured were on a 4-hour cycle, so assuming a single constant would have been wrong
        // for most of the venue.
        var funding = await _client.GetFundingInfoAsync(ct);
        var intervalBySymbol = funding.ToDictionary(f => f.Symbol, StringComparer.Ordinal);

        var list = new List<Instrument>(symbols.Count);
        foreach (var s in symbols)
        {
            if (!BinanceMarkets.IsCarriedContract(s))
            {
                ReportUnknownContractType(s);
                continue;
            }

            if (!BinanceMarkets.IsInScope(s))
            {
                continue;   // in-scope contract type, out-of-scope quote — see BinanceMarkets.UsdFamily
            }

            var price = Filter(s, "PRICE_FILTER");
            var lot = Filter(s, "LOT_SIZE");

            list.Add(new Instrument(
                ExchangeSymbol: s.Symbol,
                // The venue's own spelling, unnormalised: '1000PEPE', not 'PEPE'. The alias table
                // resolves it and folds the 1000 into the multiplier — doing it here would put a
                // second, private opinion about asset identity in the tree.
                BaseAssetRaw: s.BaseAsset,
                QuoteAssetRaw: s.QuoteAsset,
                // Quantities on USDⓈ-M are already in base-asset units (BTCUSDT trades in BTC to
                // three decimals), so there is no per-contract scaling to fold in. Where a contract
                // covers 1000 units the venue says so in the SYMBOL rather than in a multiplier
                // field, which is why the raw base above carries the prefix.
                ContractMultiplier: 1m,
                PriceStep: Decimal(price.TickSize, s, "PRICE_FILTER.tickSize"),
                QtyStep: Decimal(lot.StepSize, s, "LOT_SIZE.stepSize"),
                MinQty: Decimal(lot.MinQty, s, "LOT_SIZE.minQty"),
                // Unlike Kraken, Binance does define one — but not for every symbol, so a missing
                // MIN_NOTIONAL is null ("the venue does not define one") rather than zero.
                MinNotional: MinNotional(s),
                FundingIntervalHours: intervalBySymbol.TryGetValue(s.Symbol, out var f) && f.FundingIntervalHours > 0
                    ? (short)f.FundingIntervalHours
                    : BinanceMarkets.DefaultFundingIntervalHours,
                ListedAt: s.OnboardDate > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(s.OnboardDate) : null,
                // Throws on a status this adapter has never seen. See BinanceMarkets.Status for why
                // that is the right answer here and the wrong one for an unknown contract type.
                Status: BinanceMarkets.Status(s),
                RawJson: s.RawJson));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        // Three batched calls, 55 weight for the whole venue. Order does not matter; they are
        // sequential rather than concurrent because the venue ceiling is shared and a burst of three
        // buys nothing at this cadence.
        var books = await _client.GetBookTickersAsync(ct);
        var premiums = await _client.GetPremiumIndexAsync(ct);
        var stats = await _client.GetTicker24hAsync(ct);

        // OUR receive time, one instant for the pass, and this line is load-bearing.
        //
        // The tempting alternative is to derive received_at from the venue's own clocks — each of the
        // three responses carries one — and to take the oldest of them so the row is never claimed
        // fresher than its stalest ingredient. That reasoning is sound about freshness and wrong
        // about storage: market_snapshot is keyed on (exchange_instrument_id, received_at) with
        // ON CONFLICT DO NOTHING (SnapshotCollector), so any received_at that can REPEAT turns the
        // insert into a silent no-op. bookTicker.time only moves when the top of book moves, so on a
        // quiet symbol two consecutive passes present the same instant and the second observation is
        // dropped without a trace — precisely the class of loss already found once on Kraken's clock.
        // Our own read moves every pass by construction, and the venue's clocks are not thrown away:
        // open_interest_at carries the venue's own sampling time, which is the one place the schema
        // asks for it.
        var now = _clock.GetUtcNow();

        var premiumBySymbol = premiums.ToDictionary(p => p.Symbol, StringComparer.Ordinal);
        var statsBySymbol = stats.ToDictionary(t => t.Symbol, StringComparer.Ordinal);

        var list = new List<Ticker>(books.Count);
        foreach (var b in books)
        {
            if (!premiumBySymbol.TryGetValue(b.Symbol, out var premium))
            {
                continue;   // no mark/index/funding this round
            }

            if (!statsBySymbol.TryGetValue(b.Symbol, out var stat))
            {
                continue;   // no 24 h row this round
            }

            // Not only "no sample yet". The OI cycle's symbol list is filtered by the same scope rule
            // discovery applies, so a symbol outside our scope — an equity perpetual, a quarterly —
            // is never sampled at all and therefore never reaches the batch. That is the second of
            // two independent gates and not the primary one: the primary gate is discovery, and
            // SnapshotCollector ignores any ticker whose symbol it has no instrument row for.
            if (!_openInterest.TryGet(b.Symbol, out var oi, out var oiAt))
            {
                continue;
            }

            list.Add(new Ticker(
                ExchangeSymbol: b.Symbol,
                ReceivedAt: now,
                LastPrice: Parse(stat.LastPrice),
                BidPrice: Parse(b.BidPrice),
                AskPrice: Parse(b.AskPrice),
                BidSize: Parse(b.BidQty),
                AskSize: Parse(b.AskQty),
                MarkPrice: Parse(premium.MarkPrice),
                IndexPrice: Parse(premium.IndexPrice),
                // Already a fraction of notional per interval — no conversion, unlike Kraken's
                // absolute rate, and no fabricated zero when it is missing (it never is on this
                // endpoint; if it ever were, Parse would throw rather than default).
                FundingRate: Parse(premium.LastFundingRate),
                Turnover24h: Parse(stat.QuoteVolume),
                OpenInterest: oi,
                // Its own, older time — the venue's sampling instant from the background cycle,
                // which is what open_interest_at is for.
                OpenInterestAt: oiAt,
                // The book is a separate per-symbol concern; see GetOrderBookAsync and DepthCollector.
                Depth: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetKlines1mAsync(
            exchangeSymbol, from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds(), ct);

        var list = new List<Candle>(rows.Count);
        foreach (var r in rows)
        {
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(r[0].GetInt64());

            // Closed bars only: a 1-minute bar opened at T closes at T+60 s, so the one covering `to`
            // is still forming and is dropped. Binance returns the forming bar as a normal row with
            // partial volume, which is exactly the value that would be wrong forever once stored.
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Parse(r[1].GetString()!),
                High: Parse(r[2].GetString()!),
                Low: Parse(r[3].GetString()!),
                Close: Parse(r[4].GetString()!),
                // Index 5 is base-asset volume; index 7 is the quote value, which is not what the
                // schema wants.
                Volume: Parse(r[5].GetString()!),
                // Index 8. Binance is the first venue here that reports one at all — Kraken and WEEX
                // both carry null — so the column finally gets a real value rather than a default.
                TradeCount: r[8].GetInt32()));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetFundingHistoryAsync(
            exchangeSymbol, from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds(), ct);

        var list = new List<FundingRate>(rows.Count);
        foreach (var r in rows)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.FundingTime);

            // The window is already applied server-side; re-applied here because the caller's
            // contract is "payments in [from, to]" and a venue that widens its interpretation of the
            // bounds must not be able to widen ours.
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
        // WS first: depth off the live book, which reaches as deep as the venue publishes. Falls
        // through to REST when the feed is unhealthy, or when this symbol's book is unseeded, dirty,
        // or has not yet been seeded deep enough to answer honestly.
        if (_ws is not null && _ws.TryGetDepth(exchangeSymbol, out var live))
        {
            return live;
        }

        var response = await _client.GetDepthAsync(exchangeSymbol, BinanceUsdmClient.DepthLimit, ct);
        var bids = (response.Bids ?? []).ConvertAll(l => (Parse(l[0]), Parse(l[1])));
        var asks = (response.Asks ?? []).ConvertAll(l => (Parse(l[0]), Parse(l[1])));

        // The venue's event clock for the book, not ours: depth_at is a separate column precisely
        // because the book runs on its own schedule, and this response says when it was taken.
        var at = response.EventTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(response.EventTime)
            : _clock.GetUtcNow();

        return DepthMath.Compute(bids, asks, at);
    }

    /// <summary>
    /// Says out loud, once per distinct value per process, that a contract type was skipped.
    ///
    /// The allowlist in <see cref="BinanceMarkets"/> is what keeps an unknown type out; this is what
    /// keeps it from being invisible. TRADIFI_PERPETUAL is the standing example — 191 equity and
    /// metal perpetuals appearing under a value that is in no documentation we can cite — and the
    /// failure mode to avoid is not admitting them, it is admitting or excluding them for years
    /// without anyone knowing the choice was being made.
    /// </summary>
    private void ReportUnknownContractType(BinanceSymbol s)
    {
        lock (_reportedContractTypes)
        {
            if (!_reportedContractTypes.Add(s.ContractType))
            {
                return;
            }
        }

        _log.LogInformation(
            "Binance USDⓈ-M lists contractType '{ContractType}' (e.g. {Symbol}), which this adapter does "
            + "not carry; skipped. Add it to BinanceMarkets.CarriedContractTypes if it belongs in scope.",
            s.ContractType, s.Symbol);
    }

    /// <summary>Binance does not give the trading increments their own fields — they live inside the
    /// <c>filters</c> array, identified by <c>filterType</c>. A missing one is a throw rather than a
    /// default: price_step and qty_step carry CHECK (&gt; 0) in the schema, so a defaulted 0 would
    /// fail at the database anyway, one layer further from the cause.</summary>
    private static BinanceFilter Filter(BinanceSymbol s, string type) =>
        s.Filters.FirstOrDefault(f => string.Equals(f.FilterType, type, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"Binance USDⓈ-M returned {s.Symbol} with no {type} filter; its trading increments cannot be read.");

    /// <summary>Null where the venue defines no minimum order value, exactly as the record documents.
    /// Every in-scope symbol carried one when this was captured, so the null branch is a contract
    /// rather than an observation.</summary>
    private static decimal? MinNotional(BinanceSymbol s)
    {
        var filter = s.Filters.FirstOrDefault(f => string.Equals(f.FilterType, "MIN_NOTIONAL", StringComparison.Ordinal));
        return filter?.Notional is { } n ? decimal.Parse(n, CultureInfo.InvariantCulture) : null;
    }

    private static decimal Decimal(string? value, BinanceSymbol s, string what) =>
        value is not null
            ? decimal.Parse(value, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Binance USDⓈ-M returned {s.Symbol} with no {what}.");

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}

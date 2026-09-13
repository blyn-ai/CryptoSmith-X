using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Dydx;

/// <summary>
/// dYdX v4 perpetuals as an <see cref="IExchangeMarketData"/> — the first DEX with a real order
/// book, and so the first whose bid, ask, sizes and depth are measurements rather than a derivation.
///
/// <b>What comes from where.</b> The indexer splits a market across four routes, and the snapshot
/// row is assembled from what each of them publishes rather than from any one of them:
///
///   perpetualMarkets (one call, the whole venue)  oracle price, 24h USD volume, open interest in the
///                                                 base asset, the rate for the next hour
///   orderbooks (per market)                       bid, ask, their sizes, the depth bands, the reach
///   trades (per market)                           the last price, the instant it traded, liquidations
///   candles 1HOUR (per market)                    24h base volume, open interest by the hour
///   historicalFunding (per market)                the settled rate in force
///
/// The per-market routes are read on the depth pass, which is paced by the gate, and remembered for
/// the snapshot — so the snapshot stays one bulk call and every figure it carries was measured, with
/// its age showing in the page's own freshness columns.
///
/// <b>Mark and index are the oracle price, and that is the protocol rather than a shortcut.</b> dYdX
/// v4 marks every position against its oracle price and settles funding against the same price;
/// there is no separate mark to publish and no separate index behind it. Printing the oracle price in
/// both columns states what the venue uses in both roles.
/// </summary>
public sealed class DydxPerpMarketData : IExchangeMarketData
{
    /// <summary>How often the 24h base volume is refetched per market. It moves slowly and costs a
    /// call; ten minutes keeps it current to within a sixth of an hour against a 24-hour window.</summary>
    private static readonly TimeSpan BaseVolumeRefresh = TimeSpan.FromMinutes(10);

    /// <summary>Prints per page when walking back for liquidations: a day of BTC-USD on one page,
    /// measured.</summary>
    private const int TradePage = 1000;

    /// <summary>Prints per poll on the depth pass. BTC-USD printed 997 times in the day this was
    /// measured — under one a minute — so a hundred covers far more than the pass interval on every
    /// market here, at a tenth of the payload of a full page every minute.</summary>
    private const int PollPage = 100;

    /// <summary>Pages walked backwards when counting liquidations over a window. Ten pages is ten
    /// thousand prints — ten days of BTC-USD; hours the walk did not reach are not reported.</summary>
    private const int MaxTradePages = 10;

    private readonly DydxClient _client;
    private readonly RestTape _tape = new();
    private readonly ConcurrentDictionary<string, BookTop> _top = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (double Base, DateTimeOffset At)> _baseVolume = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _settled = new(StringComparer.Ordinal);

    public DydxPerpMarketData(DydxClient client) => _client = client;

    public string SegmentCode => "dydx-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("liquidations", "rest"),
        new("open_interest", "rest"),
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var markets = (await _client.GetMarketsAsync(ct)).Markets ?? new Dictionary<string, DydxMarket>();

        var list = new List<Instrument>(markets.Count);
        foreach (var m in markets.Values)
        {
            // "BTC-USD": the base is the venue's own spelling, left for the alias table to resolve
            // the way every other adapter leaves it; the quote is USD, settled in USDC.
            var dash = m.Ticker.LastIndexOf('-');
            if (dash <= 0 || dash == m.Ticker.Length - 1)
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: m.Ticker,
                BaseAssetRaw: m.Ticker[..dash],
                QuoteAssetRaw: m.Ticker[(dash + 1)..],
                // Sizes on the book, the tape and open interest are all base-asset units.
                ContractMultiplier: 1m,
                PriceStep: Decimal(m.TickSize),
                QtyStep: Decimal(m.StepSize),
                // The smallest order the venue accepts is one step.
                MinQty: Decimal(m.StepSize),
                MinNotional: null,
                // Funding settles every hour on this venue; historicalFunding carries one row per hour.
                FundingIntervalHours: 1,
                ListedAt: null,
                Status: Status(m),
                RawJson: JsonSerializer.Serialize(m)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var markets = (await _client.GetMarketsAsync(ct)).Markets ?? new Dictionary<string, DydxMarket>();
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(markets.Count);
        foreach (var m in markets.Values)
        {
            var top = _top.GetValueOrDefault(m.Ticker);
            var last = _tape.Last(m.Ticker);
            var oracle = Num(m.OraclePrice);

            list.Add(new Ticker(
                ExchangeSymbol: m.Ticker,
                ReceivedAt: now,
                LastPrice: last?.Price,
                LastTradeAt: last?.At,
                // The book's top as the depth pass last read it. Null until that pass has run —
                // never the oracle price standing in for a quote nobody placed.
                BidPrice: top?.BidPrice,
                AskPrice: top?.AskPrice,
                BidSize: top?.BidSize,
                AskSize: top?.AskSize,
                MarkPrice: oracle,
                IndexPrice: oracle,
                // The settled rate in force, remembered from the funding pass. The per-hour rate this
                // route carries is the NEXT period's, and it goes to the column that means that.
                FundingRate: _settled.TryGetValue(m.Ticker, out var settled) ? settled : null,
                FundingRatePredicted: Num(m.NextFundingRate),
                Turnover24h: Num(m.Volume24H),
                Volume24hBase: _baseVolume.TryGetValue(m.Ticker, out var bv) ? bv.Base : null,
                OpenInterest: Num(m.OpenInterest),
                OpenInterestAt: now,
                Depth: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = (await _client.GetCandlesAsync(exchangeSymbol, "1MIN", from, to, 100, ct)).Candles ?? [];

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.StartedAt + TimeSpan.FromMinutes(1) > to
                || Num(c.Open) is not { } o || Num(c.High) is not { } h
                || Num(c.Low) is not { } l || Num(c.Close) is not { } cl)
            {
                continue;
            }

            list.Add(new Candle(
                exchangeSymbol, c.StartedAt, o, h, l, cl,
                Volume: Num(c.BaseTokenVolume) ?? 0,
                TradeCount: c.Trades,
                VolumeQuote: Num(c.UsdVolume)));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = (await _client.GetFundingAsync(exchangeSymbol, to, ct)).HistoricalFunding ?? [];

        var list = new List<FundingRate>(rows.Count);
        DateTimeOffset? newest = null;
        foreach (var r in rows)
        {
            if (r.EffectiveAt is not { } at || Num(r.Rate) is not { } rate)
            {
                continue;
            }

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

    /// <summary>
    /// The book, and with it the tape and the 24h base volume — the per-market routes, read on the
    /// one pass the gate paces.
    /// </summary>
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
            var trades = (await _client.GetTradesAsync(exchangeSymbol, PollPage, null, ct)).Trades ?? [];
            Fold(exchangeSymbol, trades);
        }
        catch (HttpRequestException)
        {
            // The book stands; the gate has been told if this was a refusal.
        }

        if (!_baseVolume.TryGetValue(exchangeSymbol, out var bv) || now - bv.At >= BaseVolumeRefresh)
        {
            try
            {
                var hours = (await _client.GetCandlesAsync(
                    exchangeSymbol, "1HOUR", now - TimeSpan.FromHours(24), now, 24, ct)).Candles ?? [];

                // The venue's own hourly bars over the last day, summed. Not the same window as
                // volume24H to the minute — the newest bar is still forming — and said so rather than
                // scaled to agree.
                _baseVolume[exchangeSymbol] = (hours.Sum(c => Num(c.BaseTokenVolume) ?? 0), now);
            }
            catch (HttpRequestException)
            {
            }
        }

        // No timestamp on this route; the band is stamped with when we read it.
        return DepthMath.Compute(bids, asks, now);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    /// <summary>
    /// Open interest by the hour, from the venue's own bars.
    ///
    /// Each 1HOUR bar carries <c>startingOpenInterest</c> — the open interest at the instant it
    /// opened, which is the closing open interest of the hour before. So a bar opening at T yields a
    /// bucket for [T−1h, T). Stamping it on the bar that carries it would put every value one hour
    /// late.
    /// </summary>
    public async Task<IReadOnlyList<OpenInterestBucket>> GetOpenInterestHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var bars = (await _client.GetCandlesAsync(
            exchangeSymbol, "1HOUR", from, to + TimeSpan.FromHours(1), 100, ct)).Candles ?? [];

        var list = new List<OpenInterestBucket>(bars.Count);
        foreach (var c in bars)
        {
            if (Num(c.StartingOpenInterest) is not { } oi)
            {
                continue;
            }

            var bucket = c.StartedAt - TimeSpan.FromHours(1);
            if (bucket < from || bucket > to)
            {
                continue;
            }

            list.Add(new OpenInterestBucket(
                exchangeSymbol, 3600, bucket,
                Open: null, High: null, Low: null, Close: oi, Quote: null, Source: "analytics"));
        }

        return list;
    }

    /// <summary>
    /// Liquidated size per hour, counted off the venue's own tape — every print marked LIQUIDATED.
    ///
    /// <b>An hour with none is reported as zero, and only an hour the walk actually covered.</b> The
    /// tape is walked backwards from now until it reaches <paramref name="from"/> or runs out of pages;
    /// every hour from the oldest print reached forward was seen whole, so a zero there is a count of
    /// nothing rather than an absence of looking. Hours older than the walk reached are not returned.
    /// DELEVERAGED prints are a different mechanism and are not counted.
    /// </summary>
    public async Task<IReadOnlyList<LiquidationBucket>> GetLiquidationVolumeAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sums = new Dictionary<DateTimeOffset, double>();
        var counted = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? before = null;
        var reached = to;
        var exhausted = false;

        for (var page = 0; page < MaxTradePages; page++)
        {
            var trades = (await _client.GetTradesAsync(exchangeSymbol, TradePage, before, ct)).Trades ?? [];
            if (trades.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var t in trades)
            {
                if (t.CreatedAt is not { } at)
                {
                    continue;
                }

                if (at < reached)
                {
                    reached = at;
                }

                if (string.Equals(t.Type, "LIQUIDATED", StringComparison.Ordinal)
                    && at >= from && at <= to && Num(t.Size) is { } size
                    && t.Id is { Length: > 0 } id && counted.Add(id))
                {
                    var hour = FloorHour(at);
                    sums[hour] = sums.GetValueOrDefault(hour) + size;
                }
            }

            if (reached <= from || trades.Count < TradePage)
            {
                exhausted = trades.Count < TradePage;
                break;
            }

            // The oldest print's instant again, inclusive: a page boundary inside one millisecond
            // must not drop the prints that share it. Those prints come back on the next page and
            // are counted once, by id.
            before = reached;
        }

        // First hour seen whole: the one after the oldest print reached — or `from`'s own hour when
        // the tape ran out, because then there was nothing older to miss.
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

    private void Fold(string exchangeSymbol, IReadOnlyList<DydxTrade> trades)
    {
        var events = new List<TradeEvent>(trades.Count);

        // OLDEST FIRST, deliberately. The venue lists newest first, and several prints routinely share
        // one millisecond — three of them at 11:25:21.067 in the captured fixture, at 76 657, 76 653 and
        // 76 653. The tape keeps the last print it is shown among equal instants, so walking the
        // venue's own order would leave the OLDEST of the three as "last" and put a price four dollars
        // stale in the Last column.
        foreach (var t in trades.Reverse())
        {
            if (t.Id is not { Length: > 0 } id || Num(t.Price) is not { } price || price <= 0
                || Num(t.Size) is not { } size || t.CreatedAt is not { } at)
            {
                continue;
            }

            events.Add(new TradeEvent(
                exchangeSymbol, at, id, null, price, size,
                string.Equals(t.Side, "SELL", StringComparison.Ordinal) ? "sell" : "buy",
                // The venue's own classification of the print, carried through as it came.
                t.Type is { Length: > 0 } type ? type.ToLowerInvariant() : null));

            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    private static List<(double Price, double Qty)> Levels(IReadOnlyList<DydxLevel>? raw)
    {
        var levels = new List<(double Price, double Qty)>(raw?.Count ?? 0);
        foreach (var l in raw ?? [])
        {
            if (Num(l.Price) is { } px && px > 0 && Num(l.Size) is { } qty && qty > 0)
            {
                levels.Add((px, qty));
            }
        }

        return levels;
    }

    /// <summary>
    /// The venue's market status. 78 ACTIVE and 218 FINAL_SETTLEMENT when this was captured;
    /// FINAL_SETTLEMENT is terminal — the market is being wound down and will not trade again.
    /// </summary>
    private static InstrumentStatus Status(DydxMarket m) => m.Status switch
    {
        "ACTIVE" => InstrumentStatus.Trading,
        "PAUSED" or "CANCEL_ONLY" or "POST_ONLY" or "INITIALIZING" => InstrumentStatus.Halted,
        "FINAL_SETTLEMENT" => InstrumentStatus.Delisted,
        _ => throw new InvalidOperationException(
            $"dYdX reported status '{m.Status}' for {m.Ticker}, which this adapter has never seen"),
    };

    private static DateTimeOffset FloorHour(DateTimeOffset at) =>
        DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / 3600 * 3600);

    private static decimal? Decimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private sealed record BookTop(double BidPrice, double BidSize, double AskPrice, double AskSize);
}

using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Bybit;

/// <summary>
/// Bybit's spot market as an <see cref="IExchangeMarketData"/>.
///
/// <b>The same client, a different category, and NOT the same shapes.</b> Every v5 market route
/// takes <c>category</c>, so <see cref="BybitClient"/> serves both surfaces — but what comes back
/// differs in three places, each of which would produce a plausible wrong number rather than an
/// error:
///
///   * <c>lotSizeFilter</c> says <c>basePrecision</c> and <c>minOrderAmt</c> where linear says
///     <c>qtyStep</c> and <c>minNotionalValue</c>. Read the linear names against a spot row and
///     every quantity step is null.
///   * the ticker carries <c>usdIndexPrice</c> and no <c>indexPrice</c>. The two are not the same
///     measurement, and this adapter stores neither — see <see cref="GetTickersAsync"/>.
///   * kline rows are seven fields with no trade count, where linear's are the same seven. That one
///     agrees, and it is checked rather than assumed.
///
/// <b>Spot is a perpetual minus seven figures</b>, and they are absent rather than uncollected:
/// mark, index, funding, predicted funding, next funding, open interest, liquidations. The routes
/// that would carry them — <c>funding/history</c> and <c>open-interest</c> — are not called at all,
/// because for this category they do not exist.
///
/// <b>One host, one budget.</b> api.bybit.com serves both surfaces, so this segment shares a gate
/// with bybit-perp by design (0054). It adds instruments to an existing ceiling rather than bringing
/// one of its own, and that is measured, not assumed.
/// </summary>
public sealed class BybitSpotMarketData : IExchangeMarketData
{
    private readonly BybitClient _client;
    private readonly RestTape _tape = new();

    public BybitSpotMarketData(BybitClient client) => _client = client;

    public string SegmentCode => "bybit-spot";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var page = await _client.GetInstrumentsAsync(ct);
        var rows = page.List ?? [];

        var list = new List<Instrument>(rows.Count);
        foreach (var i in rows)
        {
            if (i.BaseCoin is not { Length: > 0 } b || i.QuoteCoin is not { Length: > 0 } q)
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: i.Symbol,
                BaseAssetRaw: b,
                QuoteAssetRaw: q,
                // Base-asset units. Nothing on spot is a contract.
                ContractMultiplier: 1m,
                PriceStep: Decimal(i.PriceFilter?.TickSize),
                // SPOT's spelling. basePrecision is the finest quantity the venue accepts, which is
                // what qtyStep means on the other surface — and reading the linear name here would
                // leave every step null on all 538 rows.
                QtyStep: Decimal(i.LotSizeFilter?.BasePrecision),
                MinQty: Decimal(i.LotSizeFilter?.MinOrderQty),
                MinNotional: Decimal(i.LotSizeFilter?.MinOrderAmt),
                // A spot pair pays no funding.
                FundingIntervalHours: null,
                // instruments-info carries no launch time on this category — linear has one, spot
                // does not.
                ListedAt: null,
                Status: Status(i),
                RawJson: JsonSerializer.Serialize(i)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var page = await _client.GetTickersAsync(ct);
        var rows = page.List ?? [];
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(rows.Count);
        foreach (var t in rows)
        {
            list.Add(new Ticker(
                ExchangeSymbol: t.Symbol,
                ReceivedAt: now,
                LastTradeAt: _tape.Last(t.Symbol)?.At,
                LastPrice: Num(t.LastPrice),
                BidPrice: Num(t.Bid1Price),
                AskPrice: Num(t.Ask1Price),
                BidSize: Num(t.Bid1Size),
                AskSize: Num(t.Ask1Size),
                // ABSENT. The spot ticker carries no mark and no index — what it does carry is
                // `usdIndexPrice`, the venue's USD valuation of the BASE COIN for margin purposes.
                // That is a different measurement from a perpetual's index, which tracks the pair;
                // on BTCUSDC the two would be close enough to read as right and wrong by whatever
                // USDC is worth that day. It is not stored under either name.
                MarkPrice: null,
                IndexPrice: null,
                FundingRate: null,
                Turnover24h: Num(t.Turnover24h),
                OpenInterest: null,
                OpenInterestAt: null,
                Depth: null,
                // The spot ticker carries no clock of its own — linear's does not either, and the
                // perp adapter says the same thing in the same place.
                VenueTs: null,
                Volume24hBase: Num(t.Volume24h)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var page = await _client.GetKline1mAsync(exchangeSymbol, from, to, ct);
        var rows = page.List ?? [];

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.Length < 7 || Instant(c[0]) is not { } openTime)
            {
                continue;
            }

            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Req(c[1]),
                High: Req(c[2]),
                Low: Req(c[3]),
                Close: Req(c[4]),
                Volume: Num(c[5]) ?? 0,
                // Seven fields and no count of prints — the same shape linear returns, and neither
                // surface reports one.
                TradeCount: null,
                VolumeQuote: Num(c[6])));
        }

        return list;
    }

    /// <summary>A spot market pays no funding. The route does not exist for this category and is
    /// never called.</summary>
    public Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FundingRate>>([]);

    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var book = await _client.GetOrderBookAsync(exchangeSymbol, ct);

        try
        {
            var trades = await _client.GetRecentTradesAsync(exchangeSymbol, ct);
            Fold(exchangeSymbol, trades.List ?? []);
        }
        catch (HttpRequestException)
        {
            // The book still stands, and the gate has already been told to slow down if that is
            // what happened.
        }

        // Base units, so the levels go through unscaled.
        return DepthMath.Compute(
            Levels(book.Bids),
            Levels(book.Asks),
            at: book.Ts is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    private void Fold(string exchangeSymbol, IReadOnlyList<BybitTrade> trades)
    {
        var events = new List<TradeEvent>(trades.Count);
        foreach (var t in trades)
        {
            if (t.ExecId is not { Length: > 0 } id
                || Num(t.Price) is not { } price || price <= 0
                || Num(t.Size) is not { } size
                || Instant(t.Time) is not { } at)
            {
                continue;
            }

            events.Add(new TradeEvent(
                exchangeSymbol, at, id, null, price, size,
                // 'Buy'/'Sell' is the TAKER's side on this venue, stated plainly rather than
                // encoded in a maker flag the way Binance does it.
                string.Equals(t.Side, "Sell", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy",
                null));

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

    /// <summary>
    /// The venue's listing state. 'Trading' is the only value all 538 spot rows carried when this
    /// was captured; anything else stops discovery rather than being guessed at, the same discipline
    /// the perpetual side applies on the same venue and for the same reason — a silent omission
    /// becomes a delisting nobody announced.
    /// </summary>
    private static InstrumentStatus Status(BybitInstrument i) => i.Status switch
    {
        "Trading" => InstrumentStatus.Trading,
        "PreLaunch" or "Delivering" or "Closed" => InstrumentStatus.Halted,
        _ => throw new InvalidOperationException(
            $"Bybit spot reported status '{i.Status}' for {i.Symbol}, which this adapter has never seen"),
    };

    private static decimal? Decimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"Bybit spot returned a bar with no price in it: '{s}'");

    private static DateTimeOffset? Instant(string? ms) =>
        long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(v)
            : null;
}

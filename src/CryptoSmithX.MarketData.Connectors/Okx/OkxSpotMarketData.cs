using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Okx;

/// <summary>
/// OKX's spot market as an <see cref="IExchangeMarketData"/>.
///
/// <b>This surface adds no capacity, and that is measured rather than assumed.</b> www.okx.com
/// serves SWAP and SPOT alike, and the ceiling is shared: twenty concurrent <c>tickers</c> split
/// across the two surfaces returned ten successes each. So this segment shares a gate with okx-perp
/// by design (0054, 0058) — it adds instruments under an existing ceiling and brings none of its
/// own. <b>Whoever finds that ceiling low and reaches to raise it for spot should know it is not
/// spot's to raise</b>: the number belongs to the host, the perpetual side is already spending it,
/// and this venue answers "too fast" as a code under an HTTP 200 that the pacing had to be taught
/// to read.
///
/// <b>Spot is a perpetual minus seven figures</b>, absent rather than uncollected: mark, index,
/// funding, predicted funding, next funding, open interest, liquidations. The four routes that would
/// carry them are not called at all — for <c>instType=SPOT</c> they do not exist.
///
/// <b>The trap is a field that keeps its name and changes its meaning.</b> <c>volCcy24h</c> is the
/// BASE volume on a swap and the QUOTE volume on spot; see <see cref="OkxTicker"/>.
/// </summary>
public sealed class OkxSpotMarketData : IExchangeMarketData
{
    private readonly OkxClient _client;
    private readonly RestTape _tape = new();

    public OkxSpotMarketData(OkxClient client) => _client = client;

    public string SegmentCode => "okx-spot";

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
        var rows = await _client.GetInstrumentsAsync(ct);

        var list = new List<Instrument>(rows.Count);
        foreach (var i in rows)
        {
            // SPOT names its two sides outright. A swap carries neither and has to be read through
            // instFamily and settleCcy instead, which is why this is not the perpetual adapter with
            // a different query string.
            if (i.BaseCcy is not { Length: > 0 } b || i.QuoteCcy is not { Length: > 0 } q)
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: i.InstId,
                BaseAssetRaw: b,
                QuoteAssetRaw: q,
                // Base-asset units. ctVal and ctMult are empty on every spot row — there is no
                // contract for them to describe.
                ContractMultiplier: 1m,
                PriceStep: Decimal(i.TickSz),
                QtyStep: Decimal(i.LotSz),
                MinQty: Decimal(i.MinSz),
                // This route states no minimum order value on spot; maxLmtAmt bounds the other end
                // and is not the same statement.
                MinNotional: null,
                FundingIntervalHours: null,
                ListedAt: Instant(i.ListTime),
                Status: Status(i),
                RawJson: JsonSerializer.Serialize(i)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var rows = await _client.GetTickersAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(rows.Count);
        foreach (var t in rows)
        {
            list.Add(new Ticker(
                ExchangeSymbol: t.InstId,
                ReceivedAt: now,
                LastTradeAt: _tape.Last(t.InstId)?.At,
                LastPrice: Num(t.Last),
                BidPrice: Num(t.BidPx),
                AskPrice: Num(t.AskPx),
                // Base units on spot, where on a swap the same fields are contracts.
                BidSize: Num(t.BidSz),
                AskSize: Num(t.AskSz),
                MarkPrice: null,
                IndexPrice: null,
                FundingRate: null,
                // PUBLISHED, not derived — and this is the one column where spot and swap differ by
                // more than a null. On a swap volCcy24h is the base volume and the turnover has to
                // be worked out; on spot volCcy24h IS the turnover. Carrying the swap's arithmetic
                // over here would multiply by the price a second time and put a number four orders
                // of magnitude out into a column that ranks venues against each other.
                Turnover24h: Num(t.VolCcy24h),
                OpenInterest: null,
                OpenInterestAt: null,
                Depth: null,
                VenueTs: Instant(t.Ts),
                Volume24hBase: Num(t.Vol24h)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetCandles1mAsync(exchangeSymbol, from, to, ct);

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.Length < 8 || Instant(c[0]) is not { } openTime)
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
                // Index 5 is the base volume on spot. Index 6 is volCcy and index 7 volCcyQuote, and
                // on spot those two are the same number — the quote IS the ccy here. Index 7 is read
                // because it is the one that means the same thing on both surfaces.
                Volume: Num(c[5]) ?? 0,
                TradeCount: null,
                VolumeQuote: Num(c[7])));
        }

        return list;
    }

    /// <summary>A spot market pays no funding, and <c>funding-rate</c> does not exist for
    /// <c>instType=SPOT</c>. Never called.</summary>
    public Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FundingRate>>([]);

    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var pages = await _client.GetBookAsync(exchangeSymbol, ct);
        var book = pages.FirstOrDefault();
        if (book is null)
        {
            return null;
        }

        try
        {
            Fold(exchangeSymbol, await _client.GetTradesAsync(exchangeSymbol, ct));
        }
        catch (HttpRequestException)
        {
        }

        // Base units, so the levels go through unscaled — unlike the swap surface, where a ladder is
        // in contracts and a missing multiplier is a hundredfold error.
        return DepthMath.Compute(
            Levels(book.Bids), Levels(book.Asks), Instant(book.Ts) ?? DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    private void Fold(string exchangeSymbol, IReadOnlyList<OkxTrade> trades)
    {
        var events = new List<TradeEvent>(trades.Count);
        foreach (var t in trades)
        {
            if (t.TradeId is not { Length: > 0 } id
                || Num(t.Px) is not { } price || price <= 0
                || Num(t.Sz) is not { } size
                || Instant(t.Ts) is not { } at)
            {
                continue;
            }

            events.Add(new TradeEvent(
                exchangeSymbol, at, id, null, price, size,
                // 'buy'/'sell' is the taker's own side, lower-case here where Bybit capitalises it.
                string.Equals(t.Side, "sell", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy",
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
    /// The venue's listing state. Every one of the 1 416 spot rows read 'live' when this was
    /// captured; the other values belong to the swap surface's lifecycle and are mapped where they
    /// have an obvious meaning, with anything unfamiliar stopping discovery rather than being
    /// guessed at.
    /// </summary>
    private static InstrumentStatus Status(OkxInstrument i) => i.State switch
    {
        "live" => InstrumentStatus.Trading,
        "suspend" or "preopen" or "test" => InstrumentStatus.Halted,
        "expired" => InstrumentStatus.Delisted,
        _ => throw new InvalidOperationException(
            $"OKX spot reported state '{i.State}' for {i.InstId}, which this adapter has never seen"),
    };

    private static decimal? Decimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"OKX spot returned a bar with no price in it: '{s}'");

    private static DateTimeOffset? Instant(string? ms) =>
        long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(v)
            : null;
}

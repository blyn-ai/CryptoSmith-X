using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Bybit;

/// <summary>
/// Bybit's linear perpetuals as an <see cref="IExchangeMarketData"/>. A dumb translator: the venue's
/// own symbols, the venue's own units, nothing normalised here that the Hub's asset_alias table
/// resolves later.
///
/// <b>Why this venue is cheap.</b> One call — <c>/v5/market/tickers?category=linear</c> — closes
/// every snapshot column a book venue can fill: last, both sides of the top of book WITH their
/// sizes, mark, index, funding rate and its interval, open interest in BOTH units, and 24-hour
/// volume in both units. Measured live: 873 rows, 649 KB, 0.65 s, and zero empty fields across the
/// perpetuals. There is nothing to merge and no per-symbol sweep in the snapshot path at all.
///
/// <b>Depth and the tape are REST, per symbol, on the depth sweep's own cadence.</b> The top of book
/// arrives on the ticker, so the size columns fill from the snapshot; the cumulative bands need the
/// ladder, which this venue serves 200 levels a side. The socket would be cheaper still — its
/// <c>u</c> increments by exactly one, so no REST seed and no resequencing machinery — but cheaper
/// is not the same as missing, and nothing here waits for it.
///
/// <b>Dated futures are dropped, perpetuals kept.</b> The linear category carries both; the
/// instruments endpoint names which is which in <c>contractType</c>, so the split is the venue's own
/// statement rather than a guess from the symbol's shape.
/// </summary>
public sealed class BybitPerpMarketData : IExchangeMarketData
{
    private readonly BybitClient _client;

    /// <summary>The polled tape. Filled from the depth sweep — see <see cref="GetOrderBookAsync"/>
    /// for why the two ride together — and drained by the shared TradeCollector.</summary>
    private readonly RestTape _tape = new();

    public BybitPerpMarketData(BybitClient client) => _client = client;

    public string SegmentCode => "bybit-perp";

    /// <summary>
    /// Everything this venue serves publicly. <c>book</c> is absent and stays absent: that dataset is
    /// <c>book_topn</c>, which is fed from a maintained socket book (<c>TryGetBookFrame</c>) and not
    /// from a REST ladder — declaring it would start a loop with nothing behind it. <c>liquidations</c>
    /// is absent because this venue publishes none on any public REST route; its tape carries no
    /// liquidation flag either, so there is nothing to derive one from.
    /// </summary>
    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        // The venue's own series, not our sampling of a current value — see
        // BybitClient.GetOpenInterestAsync for the measurement that says so.
        new("open_interest", "rest"),
        // The book is a REST call per symbol on the depth sweep's own cadence, and the tape rides
        // with it — see GetOrderBookAsync. No socket is claimed, because there is none.
        new("depth", "rest"),
        new("trades", "rest"),
        // Two separate routes on this venue, so two genuinely different series.
        new("candles_mark", "rest"),
        new("candles_index", "rest"),
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var page = await _client.GetInstrumentsAsync(ct);
        var rows = page.List ?? [];

        var list = new List<Instrument>(rows.Count);
        foreach (var i in rows)
        {
            // Perpetuals only. A dated future is a different instrument with an expiry, and this
            // segment is 'perp'; the venue says which is which rather than us reading the symbol.
            if (!string.Equals(i.ContractType, "LinearPerpetual", StringComparison.Ordinal))
            {
                continue;
            }

            if (i.BaseCoin is not { Length: > 0 } || i.QuoteCoin is not { Length: > 0 })
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: i.Symbol,
                BaseAssetRaw: i.BaseCoin,
                QuoteAssetRaw: i.QuoteCoin,
                // Quantities on Bybit are already in base-asset units: bid1Size on BTCUSDT reads
                // 1.528 against a 77k price, which is coins and not contracts. Nothing to scale.
                ContractMultiplier: 1m,
                PriceStep: Dec(i.PriceFilter?.TickSize),
                QtyStep: Dec(i.LotSizeFilter?.QtyStep),
                MinQty: Dec(i.LotSizeFilter?.MinOrderQty),
                MinNotional: Dec(i.LotSizeFilter?.MinNotionalValue),
                // MINUTES here — see BybitTicker.FundingIntervalHour for why this venue's two
                // spellings of the interval must never be read into each other's field.
                FundingIntervalHours: i.FundingIntervalMinutes is { } m && m > 0
                    ? (short)Math.Max(1, m / 60)
                    : null,
                ListedAt: Instant(i.LaunchTime),
                Status: i.Status switch
                {
                    "Trading" => InstrumentStatus.Trading,
                    // The venue's own words, kept as its own distinction: a contract it has closed
                    // and one it has merely paused are not the same lifecycle event.
                    "Closed" or "Delivering" => InstrumentStatus.Delisted,
                    "PreLaunch" => InstrumentStatus.Halted,
                    _ => InstrumentStatus.Halted,
                },
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
            // The ticker route carries no contract type, so a dated future reaches here too. Its
            // snapshot row is harmless — discovery never gave it an instrument id, so the collector
            // has nowhere to write it — and filtering by the symbol's shape would be a guess where
            // GetInstrumentsAsync has the venue's own answer.
            list.Add(new Ticker(
                ExchangeSymbol: t.Symbol,
                ReceivedAt: now,
                LastPrice: Num(t.LastPrice),
                BidPrice: Num(t.Bid1Price),
                AskPrice: Num(t.Ask1Price),
                BidSize: Num(t.Bid1Size),
                AskSize: Num(t.Ask1Size),
                MarkPrice: Num(t.MarkPrice),
                IndexPrice: Num(t.IndexPrice),
                FundingRate: Num(t.FundingRate),
                Turnover24h: Num(t.Turnover24h),
                OpenInterest: Num(t.OpenInterest),
                OpenInterestAt: now,
                Depth: null,
                NextFundingAt: Instant(t.NextFundingTime),
                Volume24hBase: Num(t.Volume24h),
                // Published ready-made by the venue, so it is written as published and never as our
                // own open_interest × mark — the column's whole point.
                OiQuote: Num(t.OpenInterestValue)));
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
            // [startMs, open, high, low, close, volumeBase, turnoverQuote]
            if (c.Length < 7 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(ms);

            // The bar still forming is never returned — the same rule every other adapter here
            // applies to its own newest bar.
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
                Volume: Req(c[5]),
                // Bybit publishes no per-bar trade count on this route.
                TradeCount: null,
                VolumeQuote: Num(c[6])));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var page = await _client.GetFundingHistoryAsync(exchangeSymbol, from, to, ct);
        var rows = page.List ?? [];

        var list = new List<FundingRate>(rows.Count);
        foreach (var f in rows)
        {
            if (Num(f.FundingRate) is not { } rate || Instant(f.FundingRateTimestamp) is not { } at)
            {
                continue;
            }

            list.Add(new FundingRate(exchangeSymbol, at, rate));
        }

        return list;
    }

    /// <summary>
    /// The venue's own open-interest series at <see cref="OpenInterestBucketSeconds"/> — the finest
    /// grain it publishes. The ADAPTER chooses the grain and stamps it on each bucket; there is no
    /// parameter for it, because nothing upstream knows which grains this venue serves.
    /// </summary>
    public async Task<IReadOnlyList<OpenInterestBucket>> GetOpenInterestHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        const int intervalSeconds = OpenInterestBucketSeconds;

        var page = await _client.GetOpenInterestAsync(
            exchangeSymbol, IntervalName(intervalSeconds)!, from, to, ct);
        var rows = page.List ?? [];

        var list = new List<OpenInterestBucket>(rows.Count);
        foreach (var o in rows)
        {
            if (Num(o.OpenInterest) is not { } oi || Instant(o.Timestamp) is not { } at)
            {
                continue;
            }

            list.Add(new OpenInterestBucket(
                ExchangeSymbol: exchangeSymbol,
                IntervalSeconds: intervalSeconds,
                BucketTime: at,
                // The venue publishes one point per bucket, not an OHLC of it — null is "it does not
                // aggregate", which the record's own comment keeps distinct from "not measured".
                Open: null,
                High: null,
                Low: null,
                Close: oi,
                // No quote-notional figure on this route; never our own oi × mark.
                Quote: null,
                Source: "analytics"));
        }

        return list;
    }

    /// <summary>The venue's own grain names. Only the five it publishes, and nothing mapped onto a
    /// neighbour — see the caller for why a near miss is worse than an empty answer.</summary>
    /// <summary>Five minutes — the finest open-interest grain this venue publishes.</summary>
    private const int OpenInterestBucketSeconds = 300;

    private static string? IntervalName(int seconds) => seconds switch
    {
        300 => "5min",
        900 => "15min",
        1800 => "30min",
        3600 => "1h",
        14400 => "4h",
        86400 => "1d",
        _ => null,
    };

    /// <summary>
    /// The book, and the tape with it.
    ///
    /// <b>Why the two are one call site.</b> This method is invoked by <c>DepthCollector</c> for
    /// exactly the instruments an operator has switched on — which is the symbol set the tape wants
    /// too, and the only place this adapter learns it. A separate trade loop would either need its
    /// own copy of that set or poll all 829 listings to find the 46 that matter.
    ///
    /// A tape failure never costs the book: the depth reading is what this method owes its caller,
    /// and one unreachable route must not take the other down with it.
    /// </summary>
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
            // The book still stands. See the remarks.
        }

        // Sizes are base units on this venue, so the multiplier is one and the levels go through
        // unscaled — stated rather than assumed, because three of this batch's six venues are the
        // other way.
        return ContractBook.Compute(
            Levels(book.Bids), Levels(book.Asks), contractMultiplier: 1,
            at: book.Ts is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    /// <summary>Mark or index bars. <c>[startMs, o, h, l, c]</c> — five fields, no volume, because
    /// neither series trades.</summary>
    public async Task<IReadOnlyList<PriceCandle>> GetPriceCandles1mAsync(
        string exchangeSymbol, string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (series is not ("mark" or "index"))
        {
            return [];
        }

        var page = await _client.GetPriceKline1mAsync(exchangeSymbol, series, from, to, ct);
        var rows = page.List ?? [];

        var list = new List<PriceCandle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.Length < 5 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new PriceCandle(
                exchangeSymbol, series, openTime, Req(c[1]), Req(c[2]), Req(c[3]), Req(c[4])));
        }

        return list;
    }

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
                ExchangeSymbol: exchangeSymbol,
                EventTime: at,
                VenueUid: id,
                Seq: null,
                Price: price,
                Qty: size,
                // The venue capitalises it; the comparison does not depend on that.
                TakerSide: string.Equals(t.Side, "Buy", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell",
                // This route carries no liquidation flag, so no trade here claims to be one.
                TradeType: null));

            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    /// <summary>[price, size] string pairs into levels. A malformed pair is dropped rather than
    /// zero-filled: a level at price zero would drag the band's own mid.</summary>
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

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    /// <summary>For a field the venue always fills on a bar it returned at all — a candle with no
    /// open is a malformed bar, not a market fact, and throwing names the venue rather than writing
    /// a zero that reads as a price.</summary>
    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"Bybit sent a bar with an unreadable figure: '{s}'");

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTimeOffset? Instant(string? ms) =>
        long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(v)
            : null;
}

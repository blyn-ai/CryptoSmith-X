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
/// <b>Depth is not declared yet, and that is a scope statement rather than a gap.</b> The top of
/// book arrives on the ticker, so the bid/ask SIZE columns fill from the snapshot; the cumulative
/// depth bands need a maintained book, which on this venue means the socket (its <c>u</c> increments
/// by exactly one, so no REST seed and no resequencing machinery) — the next slice, not this one.
///
/// <b>Dated futures are dropped, perpetuals kept.</b> The linear category carries both; the
/// instruments endpoint names which is which in <c>contractType</c>, so the split is the venue's own
/// statement rather than a guess from the symbol's shape.
/// </summary>
public sealed class BybitPerpMarketData : IExchangeMarketData
{
    private readonly BybitClient _client;

    public BybitPerpMarketData(BybitClient client) => _client = client;

    public string SegmentCode => "bybit-perp";

    /// <summary>
    /// What this adapter actually implements today. Depth, book and trades are absent because the
    /// socket is not wired yet — declaring them would start loops with nothing behind them, which is
    /// the precedent the fake adapter sets and the mistake the Avantis adapter had to be corrected
    /// for. They arrive with the feed.
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

    public async Task<IReadOnlyList<OpenInterestBucket>> GetOpenInterestHistoryAsync(
        string exchangeSymbol, int intervalSeconds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var interval = IntervalName(intervalSeconds);
        if (interval is null)
        {
            // A grain the venue does not publish. Empty rather than the nearest one: a bucket
            // labelled 1h holding a 4h reading is a wrong number, not a coarse one.
            return [];
        }

        var page = await _client.GetOpenInterestAsync(exchangeSymbol, interval, from, to, ct);
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

    /// <summary>Null forever: the cumulative bands need a maintained book, and the socket that
    /// carries one is not wired yet. Null is "not collected this frame", which is what the depth
    /// columns already know how to read.</summary>
    public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct) =>
        Task.FromResult<Depth?>(null);

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

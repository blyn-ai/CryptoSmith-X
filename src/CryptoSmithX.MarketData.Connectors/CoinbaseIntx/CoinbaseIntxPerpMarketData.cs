using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.CoinbaseIntx;

/// <summary>
/// Coinbase International Exchange's perpetuals as an <see cref="IExchangeMarketData"/>.
///
/// <b>The cheapest venue in the queue.</b> One public call — <c>/api/v1/instruments</c> — carries
/// the specification AND the live quote for all 264 perpetuals: both sides of the top of book with
/// their sizes, index, mark, open interest, 24-hour volume in both units, and the frame's own
/// timestamp. Discovery and the snapshot read the same response shape, because it is the same call.
///
/// <b>Funding is the one place this venue is more expensive than it looks, and the roadmap said so
/// before the code did.</b> The quote block's <c>predicted_funding</c> is a FORECAST of the next
/// rate, not the rate in force — so it is written to <c>funding_rate_predicted</c>, and
/// <c>funding_rate</c> stays absent on the snapshot. The realised series is a call per instrument
/// (<c>/instruments/{symbol}/funding</c>), which is what the funding collector's own per-symbol loop
/// is for. The honest consequence is visible on the page: the "Venue rate" column reads as
/// unmeasured here, because at snapshot time it genuinely is.
///
/// <b>Sizes are base units.</b> 4.4467 against a 77k price is coins, not contracts — nothing scales.
/// </summary>
public sealed class CoinbaseIntxPerpMarketData : IExchangeMarketData
{
    /// <summary>Nanoseconds in an hour — the unit this venue states its funding interval in.</summary>
    private const double NanosecondsPerHour = 3_600_000_000_000d;

    private readonly CoinbaseIntxClient _client;

    /// <summary>
    /// The newest SETTLED rate per symbol, learned on the funding pass and served on the snapshot.
    ///
    /// This venue publishes no realised rate in any bulk frame — the instruments route carries a
    /// forecast and nothing else, and the settled series is one call per instrument. Left at that,
    /// the funding column was empty for this venue on every row of the grid while the settled
    /// series sat in the database beside it, collected hourly.
    ///
    /// Nothing extra is asked of the venue for this: the funding collector already reads that route
    /// once per instrument per pass, and this remembers what came back. So the column is empty only
    /// until the first funding pass after a restart, and never emptier than the series itself.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _settled =
        new(StringComparer.Ordinal);

    public CoinbaseIntxPerpMarketData(CoinbaseIntxClient client) => _client = client;

    public string SegmentCode => "coinbase-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var rows = await _client.GetInstrumentsAsync(ct);

        var list = new List<Instrument>(rows.Count);
        foreach (var i in Perps(rows))
        {
            if (i.BaseAssetName is not { Length: > 0 } b || i.QuoteAssetName is not { Length: > 0 } q)
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: i.Symbol,
                BaseAssetRaw: b,
                QuoteAssetRaw: q,
                // Base units throughout — see the class remarks.
                ContractMultiplier: 1m,
                PriceStep: Dec(i.QuoteIncrement),
                QtyStep: Dec(i.BaseIncrement),
                MinQty: Dec(i.MinQuantity),
                MinNotional: Dec(i.MinNotionalValue),
                // NANOSECONDS on this venue. 3 600 000 000 000 is one hour, and this venue really
                // does fund hourly — which is why the conversion is written out rather than folded
                // into a shared helper that would make four venues look like they agree.
                FundingIntervalHours: Num(i.FundingInterval) is { } ns && ns > 0
                    ? (short)Math.Max(1, Math.Round(ns / NanosecondsPerHour))
                    : null,
                // No listing date on this route.
                ListedAt: null,
                Status: string.Equals(i.TradingState, "TRADING", StringComparison.Ordinal)
                    ? InstrumentStatus.Trading
                    : InstrumentStatus.Halted,
                RawJson: JsonSerializer.Serialize(i)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var rows = await _client.GetInstrumentsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(rows.Count);
        foreach (var i in Perps(rows))
        {
            var quote = i.Quote;

            list.Add(new Ticker(
                ExchangeSymbol: i.Symbol,
                ReceivedAt: now,
                LastPrice: Num(quote?.TradePrice),
                BidPrice: Num(quote?.BestBidPrice),
                AskPrice: Num(quote?.BestAskPrice),
                BidSize: Num(quote?.BestBidSize),
                AskSize: Num(quote?.BestAskSize),
                MarkPrice: Num(quote?.MarkPrice),
                IndexPrice: Num(quote?.IndexPrice),
                // The newest SETTLED rate, remembered from the funding pass — never the forecast
                // beside it in this same frame, which goes to the column that means a forecast.
                // See _settled.
                FundingRate: _settled.TryGetValue(i.Symbol, out var settled) ? settled : null,
                Turnover24h: Num(i.Notional24hr),
                OpenInterest: Num(i.OpenInterest),
                OpenInterestAt: now,
                Depth: null,
                VenueTs: quote?.Timestamp,
                // The forecast, in the column that means a forecast.
                FundingRatePredicted: Num(quote?.PredictedFunding),
                Volume24hBase: Num(i.Qty24hr)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var page = await _client.GetCandles1mAsync(exchangeSymbol, from, ct);
        var rows = page.Aggregations ?? [];

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.Start is not { } openTime || openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Req(c.Open),
                High: Req(c.High),
                Low: Req(c.Low),
                Close: Req(c.Close),
                Volume: Req(c.Volume),
                TradeCount: null,
                // This venue publishes no quote-notional per bar.
                VolumeQuote: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var page = await _client.GetFundingAsync(exchangeSymbol, ct);
        var rows = page.Results ?? [];

        var list = new List<FundingRate>(rows.Count);
        foreach (var f in rows)
        {
            if (f.EventTime is not { } at || Num(f.FundingRate) is not { } rate)
            {
                continue;
            }

            // The route takes no window, so it is applied here.
            if (at < from || at > to)
            {
                continue;
            }

            list.Add(new FundingRate(exchangeSymbol, at, rate));
        }

        // The rate in force, for the snapshot. Taken from the whole page rather than from the
        // windowed list, because a catch-up asking for an old window would otherwise teach the
        // snapshot an old rate.
        var newest = rows
            .Where(f => f.EventTime is not null && Num(f.FundingRate) is not null)
            .OrderByDescending(f => f.EventTime!.Value)
            .FirstOrDefault();

        if (newest is not null && Num(newest.FundingRate) is { } inForce)
        {
            _settled[exchangeSymbol] = inForce;
        }

        return list;
    }

    /// <summary>Null until a socket is wired.</summary>
    public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct) =>
        Task.FromResult<Depth?>(null);

    /// <summary>The perpetuals. The same response also carries this venue's spot listings, which
    /// belong to a segment whose kind is 'spot' and not to this one.</summary>
    private static IEnumerable<IntxInstrument> Perps(IEnumerable<IntxInstrument> rows) =>
        rows.Where(r => string.Equals(r.Type, "PERP", StringComparison.Ordinal));

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"Coinbase INTX sent a bar with an unreadable figure: '{s}'");

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

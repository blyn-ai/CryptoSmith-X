using CryptoSmithX.Studio.Models;

namespace CryptoSmithX.Studio.Tests;

/// <summary>
/// A venue row with everything unmeasured, so each test states only the figures it is about.
///
/// The defaults are all null on purpose: a test that forgets to set a value gets a dash, which is
/// "not measured", rather than a zero, which would be an observation the test never made.
/// </summary>
internal static class Rows
{
    public static PairVenueRow Venue(
        int id,
        string quote = "USDT",
        string segment = "weex-futures",
        double multiplier = 1,
        double? bid = null,
        double? ask = null,
        double? bidSize = null,
        double? askSize = null,
        double? turnover = null,
        double? openInterest = null,
        double? depthBid25 = null,
        double? depthAsk25 = null,
        double? funding = null,
        short fundingHours = 8) =>
        new(
            InstrumentId: id,
            SegmentCode: segment,
            SegmentKind: "perp",
            ExchangeCode: segment.Split('-')[0],
            ExchangeName: segment,
            Symbol: $"SYM{id}",
            BaseAsset: "BTC",
            QuoteAsset: quote,
            ContractMultiplier: multiplier,
            PriceStep: 0.1,
            QtyStep: 0.001,
            FundingIntervalHours: fundingHours,
            Status: "trading",
            StatusChangedAt: DateTime.UnixEpoch,
            FirstSeenAt: DateTime.UnixEpoch,
            ReceivedAt: DateTime.UnixEpoch,
            LastPrice: null,
            BidPrice: bid,
            AskPrice: ask,
            BidSize: bidSize,
            AskSize: askSize,
            MarkPrice: null,
            IndexPrice: null,
            FundingRate: funding,
            Turnover24h: turnover,
            OpenInterest: openInterest,
            OpenInterestAt: null,
            DepthBid10: null,
            DepthAsk10: null,
            DepthBid25: depthBid25,
            DepthAsk25: depthAsk25,
            DepthBid50: null,
            DepthAsk50: null,
            DepthAt: null);

    /// <summary>A window wide enough to be a real one and round enough to do arithmetic against:
    /// twelve of these is 360 s, so an age of 360 s is exactly the degraded boundary.</summary>
    public const double Window = 30;

    /// <summary>
    /// A row with the three ages its three calls are carrying, and one window for all three.
    ///
    /// Defaulted to zero: the call has just landed. That is the only default that lets a test about
    /// ranking say nothing about freshness and still mean something — an unstated age would
    /// otherwise have to be null, and a null age is "never observed", which is a claim the test
    /// never made.
    /// </summary>
    /// <param name="candles">The price history behind the bid, ask and last lines.</param>
    /// <param name="metrics">The hourly series behind the spread, funding, open-interest and depth
    /// 25bps lines. Empty by default, which is a venue nothing has been rolled up for — a real
    /// state, and the one that draws no line at all.</param>
    public static VenueRowModel At(
        PairVenueRow row,
        double? price = 0,
        double? openInterest = 0,
        double? depth = 0,
        double? window = Window,
        CandleSeries? candles = null,
        MetricHourSeries? metrics = null) =>
        new(row,
            new FreshnessWindows(window, window, window),
            new CallAges(price, openInterest, depth),
            candles ?? CandleSeries.Empty,
            metrics ?? MetricHourSeries.Empty);

    /// <summary>Rows whose three calls have all just landed. What a comparison looks like when
    /// freshness is not the subject of the test.</summary>
    public static IReadOnlyList<VenueRowModel> Live(params PairVenueRow[] rows) =>
        rows.Select(r => At(r)).ToList();
}

namespace CryptoSmithX.WebApp.Models;

/// <summary>
/// One platform's top of book for a pair at one instant. Every measurement carries its own lag,
/// because platforms are not observed together: poll passes are unsynchronised, a depth sweep across
/// a large venue takes minutes, and since 0020 the keep interval is set per segment. Two rows here
/// can be honest and still be minutes apart, which is why nothing on this table is differenced
/// across rows — the candles below are what share a clock.
/// </summary>
public sealed record PairVenueRow(
    int InstrumentId,
    string SegmentCode,
    string ExchangeCode,
    string ExchangeName,
    string Symbol,
    decimal ContractMultiplier,
    string Status,
    bool Collect,
    DateTime? ReceivedAt,
    double? PriceLagSeconds,
    double? BidPrice,
    double? AskPrice,
    double? LastPrice,
    double? MarkPrice,
    double? IndexPrice,
    double? BidSize,
    double? AskSize,
    double? Turnover24h,
    double? FundingRate,
    double? OpenInterest,
    double? OpenInterestLagSeconds,
    double? DepthBid10,
    double? DepthAsk10,
    double? DepthBid25,
    double? DepthAsk25,
    double? DepthBid50,
    double? DepthAsk50,
    DateTime? DepthAt,
    double? DepthLagSeconds)
{
    /// <summary>Derived, never stored: 0001 forbids a column for a number two places could compute
    /// differently. Null when either side is missing — a one-sided book has no spread.</summary>
    public double? SpreadBps =>
        BidPrice is { } b && AskPrice is { } a && b + a > 0 ? (a - b) / ((a + b) / 2) * 10000 : null;

    /// <summary>Sizes are quoted in the venue's own contract. Multiplied through, two platforms are
    /// in the same units; left raw they are not. Zero is a real observation — nothing resting at the
    /// touch — and must stay zero rather than becoming a dash.</summary>
    public double? BidSizeBase => BidSize is { } v ? v * (double)ContractMultiplier : null;
    public double? AskSizeBase => AskSize is { } v ? v * (double)ContractMultiplier : null;

    public bool Observed => ReceivedAt is not null;
}

/// <summary>
/// One window of one platform's candles. Windows are the only measurement in this system taken over
/// the same wall clock on every platform, which is what makes the charts below comparable at all.
/// </summary>
public sealed record PairCandle(
    DateTime OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    short BarCount,
    short Timeframe)
{
    public bool Complete => BarCount >= Timeframe;
    public bool Traded => Volume > 0;
    public bool Up => Close >= Open;
}

/// <summary>One platform's candle series, already aligned to the page's shared window list.</summary>
public sealed record PairVenueSeries(
    string SegmentCode,
    string Symbol,
    decimal ContractMultiplier,
    IReadOnlyList<PairCandle?> Candles)
{
    public IEnumerable<PairCandle> Present => Candles.OfType<PairCandle>();
    public int Missing => Candles.Count(c => c is null);
    public double? Low => Present.Any() ? Present.Min(c => c.Low) : null;
    public double? High => Present.Any() ? Present.Max(c => c.High) : null;
    public double? MaxVolume => Present.Any() ? Present.Max(c => c.Volume) : null;
}

/// <summary>
/// One pair across every platform that lists it, at one instant: top of book as a table, candles as
/// a chart per platform underneath, both anchored to the same windows.
/// </summary>
public sealed record PairAtInstant(
    string Base,
    string Quote,
    DateTime At,
    short Timeframe,
    IReadOnlyList<DateTime> Windows,
    IReadOnlyList<PairVenueRow> Venues,
    IReadOnlyList<PairVenueSeries> Series,
    bool SharedScale)
{
    public string Pair => $"{Base}/{Quote}";

    /// <summary>The last window that CLOSED at or before the instant. Never the window containing
    /// it: that one runs past the instant and holds trades that had not happened yet.</summary>
    public static DateTime Anchor(DateTime at, short timeframe)
    {
        var window = TimeSpan.FromMinutes(timeframe).Ticks;
        return new DateTime(at.Ticks / window * window, DateTimeKind.Utc).AddMinutes(-timeframe);
    }

    /// <summary>
    /// Platforms whose contracts are the same size. Only these may share one price axis: a chart
    /// that puts a 1× and a 100× contract on one scale is drawing a difference in contract terms and
    /// calling it a difference in price.
    /// </summary>
    public bool ComparableTerms => Series.Count < 2
        || Series.Select(s => s.ContractMultiplier).Distinct().Count() == 1;

    /// <summary>The price axis shared by every chart when terms allow it, so a candle drawn higher
    /// on screen is a higher price — the whole point of stacking them.</summary>
    public (double Low, double High)? SharedDomain
    {
        get
        {
            // Asked for per-platform scales, or the contracts are not the same size — in which case
            // one axis would be drawing a difference in contract terms as a difference in price.
            if (!SharedScale || !ComparableTerms) { return null; }
            var lows = Series.Select(s => s.Low).OfType<double>().ToList();
            var highs = Series.Select(s => s.High).OfType<double>().ToList();
            if (lows.Count == 0 || highs.Count == 0) { return null; }
            double lo = lows.Min(), hi = highs.Max();
            return hi > lo ? (lo, hi) : (lo - 1, hi + 1);
        }
    }
}

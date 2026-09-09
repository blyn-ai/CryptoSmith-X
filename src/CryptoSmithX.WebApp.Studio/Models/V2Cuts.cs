using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// The dimensions bands 2 and 3 answer, driven by one selector.
///
/// BOTH BANDS TAKE THE SAME CHOICE, and that is the point of them being two bands rather than one
/// chart: band 2 says how the venues stand against each other RIGHT NOW, band 3 says how each of
/// them got there. The first design put the selector on the lower band only and hard-wired the
/// upper one to depth, which made them read as two unrelated things and left the obvious question —
/// "why do the books not change when I pick open interest" — with no answer.
///
/// Not every dimension has a comparable "now" figure and not every one has an hourly series; a cut
/// says which it has, and the band that has nothing to draw says so in words rather than showing an
/// empty frame.
/// </summary>
public static class V2Cuts
{
    public static IReadOnlyList<V2Cut> All { get; } =
    [
        new("price", "Price", "quote asset", V2CutSource.Candles, PairColumn.Bid, 6),
        new("spread", "Spread", "bps", V2CutSource.Spread, PairColumn.SpreadBps, 2),
        new("depth25", "Depth 25 bps", "base units, both sides", V2CutSource.Depth25, PairColumn.Depth25, 0),
        new("oi", "Open interest", "base units", V2CutSource.OpenInterest, PairColumn.OpenInterest, 0),
        new("funding", "Funding", "per venue interval", V2CutSource.Funding, null, 6),
        new("turnover", "Turnover", "quote asset, rolling 24 h", V2CutSource.None, PairColumn.Turnover24h, 0),
    ];

    /// <summary>The hourly series for one cut, or an empty list when this cut keeps none.</summary>
    public static IReadOnlyList<double?> Series(VenueRowModel r, V2Cut cut) => cut.Source switch
    {
        V2CutSource.Candles => r.Candles.Closes,
        V2CutSource.Spread => r.Metrics.Spread,
        V2CutSource.Depth25 => r.Metrics.Depth25,
        V2CutSource.OpenInterest => r.Metrics.OpenInterest,
        V2CutSource.Funding => r.Metrics.Funding,
        _ => [],
    };

    /// <summary>The figure band 2 ranks the venues by, or null when this cut has no comparable
    /// "now" — funding is a rate over each venue's own interval and does not rank in one column
    /// until it is normalised, which is what the carry group in the table does.</summary>
    public static double? Now(VenueRowModel r, V2Cut cut) => cut.Column switch
    {
        PairColumn.Bid => r.Row.BidPrice,
        PairColumn.SpreadBps => r.Row.SpreadBps,
        PairColumn.Depth25 => r.Row.DepthBid25 is { } b && r.Row.DepthAsk25 is { } a ? b + a : null,
        PairColumn.OpenInterest => r.Row.OpenInterest,
        PairColumn.Turnover24h => r.Row.Turnover24h,
        _ => null,
    };
}

public sealed record V2Cut(string Key, string Name, string Unit, V2CutSource Source, PairColumn? Column, int Decimals);

public enum V2CutSource { None, Candles, Spread, Depth25, OpenInterest, Funding }

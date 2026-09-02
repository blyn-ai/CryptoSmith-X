namespace CryptoSmithX.MarketData.Hub.Rollups;

/// <summary>A stored 1-minute bar, as the rollup sees it.</summary>
public sealed record MinuteBar(
    DateTimeOffset OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    int? TradeCount);

/// <summary>A derived bar ready to be written.</summary>
public sealed record DerivedBar(
    DateTimeOffset OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    int? TradeCount,
    short BarCount);

/// <summary>
/// The aggregation rule, with no I/O in sight. <see cref="RollupJob"/> runs this same rule as a SQL
/// <c>group by</c> instead of calling these methods — a bar's minutes can be millions of rows, and
/// this class exists to state precisely, and assert directly, what that SQL is required to compute.
/// Kept and tested for that reason even though nothing in the runtime path calls it anymore.
/// </summary>
public static class Rollup
{
    /// <summary>UTC-aligned start of the window a minute belongs to.</summary>
    public static DateTimeOffset WindowStart(DateTimeOffset openTime, int timeframeMinutes)
    {
        var seconds = timeframeMinutes * 60L;
        var epoch = openTime.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(epoch - FloorMod(epoch, seconds));
    }

    /// <summary>
    /// Aggregates the minutes of one window. Open is the first minute and close the last by time,
    /// not by arrival order. bar_count is how many minutes were actually present — fewer than the
    /// timeframe means the venue had nothing for those minutes.
    /// </summary>
    public static DerivedBar Aggregate(DateTimeOffset windowStart, IReadOnlyList<MinuteBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (bars.Count == 0)
        {
            throw new ArgumentException("A window with no minutes is not a bar.", nameof(bars));
        }

        var ordered = bars.OrderBy(b => b.OpenTime).ToList();

        return new DerivedBar(
            OpenTime: windowStart,
            Open: ordered[0].Open,
            High: ordered.Max(b => b.High),
            Low: ordered.Min(b => b.Low),
            Close: ordered[^1].Close,
            Volume: ordered.Sum(b => b.Volume),
            // Summing over a window where any minute lacks the counter would invent trades.
            TradeCount: ordered.All(b => b.TradeCount.HasValue)
                ? ordered.Sum(b => b.TradeCount!.Value)
                : null,
            BarCount: (short)ordered.Count);
    }

    /// <summary>Groups loose minutes into the windows of one timeframe and aggregates each.</summary>
    public static IReadOnlyList<DerivedBar> Build(IEnumerable<MinuteBar> minutes, int timeframeMinutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        return minutes
            .GroupBy(b => WindowStart(b.OpenTime, timeframeMinutes))
            .OrderBy(g => g.Key)
            .Select(g => Aggregate(g.Key, g.ToList()))
            .ToList();
    }

    /// <summary>Modulo that stays non-negative for times before the epoch.</summary>
    private static long FloorMod(long value, long modulus)
    {
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}

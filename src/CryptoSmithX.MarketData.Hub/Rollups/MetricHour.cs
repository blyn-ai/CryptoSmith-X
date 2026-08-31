namespace CryptoSmithX.MarketData.Hub.Rollups;

/// <summary>One snapshot observation that feeds the hourly metric — the fields the hour needs.</summary>
public sealed record MetricSnapshot(
    DateTimeOffset ReceivedAt,
    double BidPrice,
    double AskPrice,
    double OpenInterest,
    double FundingRate,
    double? DepthBid25Bps,
    double? DepthAsk25Bps);

/// <summary>The aggregated hour, as written to <c>market_metric_hour</c>.</summary>
public sealed record MetricHourBar(
    DateTimeOffset HourTime,
    double OpenInterestLast,
    double FundingRateLast,
    double? SpreadBpsAvg,
    double? DepthBid25BpsAvg,
    double? DepthAsk25BpsAvg,
    short SnapshotCount);

/// <summary>
/// Collapses one instrument-hour of snapshots into a single metric row. Pure, so the arithmetic is
/// unit-tested without a database — <see cref="RollupJob"/> only reads the rows and upserts.
///
/// OI and funding take the LAST observation of the hour (a level, not a flow). Spread and depth are
/// averaged, and each guards its own validity: a crossed book (bid &gt; ask) is left out of the
/// spread average, and a null depth reading out of the depth average. Absence of any valid
/// measurement is a null, never a zero — a zero would read as "the spread was flat", not "unknown".
/// </summary>
public static class MetricHour
{
    public static MetricHourBar Aggregate(DateTimeOffset hourTime, IReadOnlyList<MetricSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("A metric hour needs at least one snapshot.", nameof(snapshots));
        }

        var last = snapshots[0];
        foreach (var s in snapshots)
        {
            if (s.ReceivedAt >= last.ReceivedAt)
            {
                last = s;
            }
        }

        double sumSpread = 0;
        var spreadCount = 0;
        foreach (var s in snapshots)
        {
            var mid = (s.BidPrice + s.AskPrice) / 2;
            // Crossed book (bid > ask) is a real venue event but not a spread; leave it out.
            if (s.AskPrice >= s.BidPrice && mid > 0)
            {
                sumSpread += (s.AskPrice - s.BidPrice) / mid * 10_000;
                spreadCount++;
            }
        }

        return new MetricHourBar(
            HourTime: hourTime,
            OpenInterestLast: last.OpenInterest,
            FundingRateLast: last.FundingRate,
            SpreadBpsAvg: spreadCount > 0 ? sumSpread / spreadCount : null,
            DepthBid25BpsAvg: Average(snapshots, s => s.DepthBid25Bps),
            DepthAsk25BpsAvg: Average(snapshots, s => s.DepthAsk25Bps),
            SnapshotCount: (short)snapshots.Count);
    }

    /// <summary>Mean of the readings that exist; null when none did.</summary>
    private static double? Average(IReadOnlyList<MetricSnapshot> snapshots, Func<MetricSnapshot, double?> pick)
    {
        double sum = 0;
        var count = 0;
        foreach (var s in snapshots)
        {
            var value = pick(s);
            if (value is not null)
            {
                sum += value.Value;
                count++;
            }
        }

        return count > 0 ? sum / count : null;
    }
}

using CryptoSmithX.MarketData.Hub.Rollups;

namespace CryptoSmithX.MarketData.Hub.Tests;

public sealed class MetricHourTests
{
    private static readonly DateTimeOffset Hour = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static MetricSnapshot Snap(
        int second, double bid, double ask, double oi, double funding,
        double? depthBid = 1000, double? depthAsk = 1000) =>
        new(Hour.AddSeconds(second), bid, ask, oi, funding, depthBid, depthAsk);

    [Fact]
    public void Open_interest_and_funding_take_the_last_observation_of_the_hour()
    {
        var bar = MetricHour.Aggregate(Hour, new[]
        {
            Snap(0,   100, 101, oi: 5_000, funding: 0.0001),
            Snap(600, 100, 101, oi: 6_000, funding: 0.0002),
            Snap(300, 100, 101, oi: 5_500, funding: 0.00015),   // out of order on purpose
        });

        Assert.Equal(6_000, bar.OpenInterestLast);      // the second=600 row is newest
        Assert.Equal(0.0002, bar.FundingRateLast);
        Assert.Equal((short)3, bar.SnapshotCount);
    }

    [Fact]
    public void Spread_is_averaged_in_basis_points()
    {
        // bid 100, ask 101 -> mid 100.5, spread 1 -> ~99.5 bps, twice.
        var bar = MetricHour.Aggregate(Hour, new[]
        {
            Snap(0, 100, 101, oi: 1, funding: 0),
            Snap(1, 100, 101, oi: 1, funding: 0),
        });

        Assert.NotNull(bar.SpreadBpsAvg);
        Assert.Equal(99.50, bar.SpreadBpsAvg!.Value, 2);
    }

    [Fact]
    public void A_crossed_book_is_left_out_of_the_spread_average()
    {
        // One clean book (bid 100, ask 101) and one crossed (bid 102 > ask 101): only the clean
        // one counts, so the average equals that single valid measurement.
        var bar = MetricHour.Aggregate(Hour, new[]
        {
            Snap(0, 100, 101, oi: 1, funding: 0),
            Snap(1, 102, 101, oi: 1, funding: 0),   // crossed — excluded
        });

        Assert.NotNull(bar.SpreadBpsAvg);
        Assert.Equal(99.50, bar.SpreadBpsAvg!.Value, 2);
    }

    [Fact]
    public void Spread_is_null_when_every_book_in_the_hour_is_crossed()
    {
        var bar = MetricHour.Aggregate(Hour, new[] { Snap(0, 102, 101, oi: 1, funding: 0) });
        Assert.Null(bar.SpreadBpsAvg);
    }

    [Fact]
    public void Depth_averages_only_the_readings_that_exist()
    {
        var bar = MetricHour.Aggregate(Hour, new[]
        {
            Snap(0, 100, 101, oi: 1, funding: 0, depthBid: 800,  depthAsk: null),
            Snap(1, 100, 101, oi: 1, funding: 0, depthBid: 1200, depthAsk: 400),
        });

        Assert.Equal(1000, bar.DepthBid25BpsAvg!.Value, 6);   // (800 + 1200) / 2
        Assert.Equal(400, bar.DepthAsk25BpsAvg!.Value, 6);    // the one non-null ask reading
    }

    [Fact]
    public void Depth_is_null_when_no_reading_in_the_hour_had_it()
    {
        var bar = MetricHour.Aggregate(Hour, new[]
        {
            Snap(0, 100, 101, oi: 1, funding: 0, depthBid: null, depthAsk: null),
        });

        Assert.Null(bar.DepthBid25BpsAvg);
        Assert.Null(bar.DepthAsk25BpsAvg);
    }

    [Fact]
    public void An_hour_with_no_snapshots_is_not_a_row()
    {
        Assert.Throws<ArgumentException>(() => MetricHour.Aggregate(Hour, Array.Empty<MetricSnapshot>()));
    }
}

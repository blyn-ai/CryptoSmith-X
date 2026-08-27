using CryptoSmithX.MarketData.Hub.Rollups;

namespace CryptoSmithX.MarketData.Hub.Tests;

public sealed class RollupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static MinuteBar Bar(int minute, double open, double high, double low, double close,
        double volume = 1, int? trades = 10) =>
        new(T0.AddMinutes(minute), open, high, low, close, volume, trades);

    [Fact]
    public void Windows_are_utc_aligned()
    {
        Assert.Equal(T0, Rollup.WindowStart(T0.AddMinutes(4), 5));
        Assert.Equal(T0.AddMinutes(5), Rollup.WindowStart(T0.AddMinutes(5), 5));
        Assert.Equal(T0, Rollup.WindowStart(T0.AddMinutes(59), 60));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            Rollup.WindowStart(T0.AddMinutes(37), 1440));
    }

    [Fact]
    public void Five_one_minute_bars_aggregate_into_one_five_minute_bar()
    {
        var bars = new[]
        {
            Bar(0, 100, 105, 99, 104, volume: 2, trades: 10),
            Bar(1, 104, 110, 103, 108, volume: 3, trades: 20),
            Bar(2, 108, 109, 101, 102, volume: 1, trades: 5),
            Bar(3, 102, 106, 100, 105, volume: 4, trades: 7),
            Bar(4, 105, 107, 104, 106, volume: 5, trades: 8),
        };

        var bar = Rollup.Aggregate(T0, bars);

        Assert.Equal(T0, bar.OpenTime);
        Assert.Equal(100, bar.Open);      // first by time
        Assert.Equal(110, bar.High);      // max
        Assert.Equal(99, bar.Low);        // min
        Assert.Equal(106, bar.Close);     // last by time
        Assert.Equal(15, bar.Volume);     // sum
        Assert.Equal(50, bar.TradeCount); // sum
        Assert.Equal((short)5, bar.BarCount);
    }

    [Fact]
    public void Open_and_close_follow_time_not_arrival_order()
    {
        var shuffled = new[] { Bar(3, 102, 106, 100, 105), Bar(0, 100, 105, 99, 104), Bar(1, 104, 110, 103, 108) };
        var bar = Rollup.Aggregate(T0, shuffled);

        Assert.Equal(100, bar.Open);
        Assert.Equal(105, bar.Close);
    }

    [Fact]
    public void A_gap_shows_up_as_a_bar_count_below_the_timeframe()
    {
        // minutes 0, 1 and 4 only — the venue never sent 2 and 3
        var bars = new[] { Bar(0, 100, 101, 99, 100), Bar(1, 100, 102, 100, 101), Bar(4, 101, 103, 100, 102) };

        var bar = Rollup.Aggregate(T0, bars);

        Assert.Equal((short)3, bar.BarCount);
        Assert.True(bar.BarCount < 5);
    }

    [Fact]
    public void Trade_count_is_null_when_any_constituent_minute_lacks_it()
    {
        var bars = new[]
        {
            Bar(0, 100, 101, 99, 100, trades: 10),
            Bar(1, 100, 102, 100, 101, trades: null),
            Bar(2, 101, 103, 100, 102, trades: 12),
        };

        Assert.Null(Rollup.Aggregate(T0, bars).TradeCount);
    }

    [Fact]
    public void Trade_count_is_summed_only_when_every_minute_reports_one()
    {
        var bars = new[] { Bar(0, 100, 101, 99, 100, trades: 10), Bar(1, 100, 102, 100, 101, trades: 12) };
        Assert.Equal(22, Rollup.Aggregate(T0, bars).TradeCount);
    }

    [Fact]
    public void A_late_minute_changes_the_parent_it_belongs_to()
    {
        // What the rollup saw first: minute 2 had not arrived.
        var before = Rollup.Aggregate(T0, [Bar(0, 100, 105, 99, 104), Bar(1, 104, 110, 103, 108)]);

        // Minute 2 turns up afterwards with a new extreme and the last close of the window.
        var after = Rollup.Aggregate(T0, [Bar(0, 100, 105, 99, 104), Bar(1, 104, 110, 103, 108), Bar(2, 108, 121, 90, 95)]);

        Assert.Equal((short)2, before.BarCount);
        Assert.Equal((short)3, after.BarCount);
        Assert.NotEqual(before.High, after.High);
        Assert.Equal(121, after.High);
        Assert.Equal(90, after.Low);
        Assert.Equal(95, after.Close);
        Assert.Equal(before.Open, after.Open);   // the open of the window does not move
    }

    [Fact]
    public void Build_groups_loose_minutes_into_their_windows()
    {
        var minutes = Enumerable.Range(0, 12).Select(m => Bar(m, 100 + m, 100 + m, 100 + m, 100 + m)).ToList();

        var bars = Rollup.Build(minutes, 5);

        Assert.Equal(3, bars.Count);
        Assert.Equal([(short)5, (short)5, (short)2], bars.Select(b => b.BarCount));
        Assert.Equal(T0, bars[0].OpenTime);
        Assert.Equal(T0.AddMinutes(10), bars[2].OpenTime);
    }

    [Fact]
    public void A_window_with_no_minutes_is_not_a_bar()
    {
        Assert.Throws<ArgumentException>(() => Rollup.Aggregate(T0, []));
    }
}

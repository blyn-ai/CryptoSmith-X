using CryptoSmithX.WebApp.Models;

namespace CryptoSmithX.WebApp.Tests;

/// <summary>
/// The three clocks the market-state page judges rows against. They were one number until 0020 made
/// the keep rate per segment, and collapsing them again is not a cosmetic mistake: judging depth by
/// the snapshot keep rate turns every depth cell on a venue gold the moment that venue starts
/// keeping every 10 s, which is exactly the configuration the feature exists to allow.
/// </summary>
public sealed class SegmentCadenceTests
{
    [Fact]
    public void Depth_is_judged_by_its_own_loop_not_by_the_keep_rate()
    {
        // Keeping every 10 s must not shrink the depth tolerance: a depth sweep across a large venue
        // is minutes wide and has nothing to do with how often snapshots are stored.
        var fast = new SegmentCadence("kraken-futures", PollSeconds: 10, KeepSeconds: 10,
            DepthPollSeconds: 60, DepthSweepSeconds: 470);
        var slow = fast with { KeepSeconds = 300 };

        Assert.Equal(fast.DepthTolerance, slow.DepthTolerance);
        Assert.True(fast.DepthTolerance > 470, "a 470 s sweep must not read as stale");
    }

    [Fact]
    public void Price_is_judged_by_the_keep_rate()
    {
        var c = new SegmentCadence("fake", 10, 60, 60, null);
        Assert.Equal(120, c.PriceTolerance);
    }

    [Fact]
    public void An_unmeasured_sweep_falls_back_to_the_depth_interval_alone()
    {
        var c = new SegmentCadence("weex-futures", 10, 10, DepthPollSeconds: 60, DepthSweepSeconds: null);
        Assert.Equal(120, c.DepthTolerance);
    }

    [Fact]
    public void A_segment_with_no_depth_loop_falls_back_to_the_keep_rate()
    {
        var c = new SegmentCadence("fake", 10, 60, DepthPollSeconds: null, DepthSweepSeconds: null);
        Assert.Equal(120, c.DepthTolerance);
    }

    [Fact]
    public void Dropping_is_keep_against_poll_not_against_a_literal()
    {
        // The old gate was "keep > 10", the snapshot poll default at the time it was written.
        // It fired when nothing was dropped...
        Assert.False(new SegmentCadence("a", PollSeconds: 60, KeepSeconds: 60, null, null).Drops);
        // ...and stayed silent when nine observations in ten were.
        Assert.True(new SegmentCadence("b", PollSeconds: 1, KeepSeconds: 10, null, null).Drops);
    }

    [Fact]
    public void Nothing_is_dropped_when_a_rate_is_unknown()
    {
        Assert.False(new SegmentCadence("c", null, 60, null, null).Drops);
        Assert.False(new SegmentCadence("d", 10, null, null, null).Drops);
    }
}

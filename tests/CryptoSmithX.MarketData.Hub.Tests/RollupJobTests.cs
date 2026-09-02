using CryptoSmithX.MarketData.Hub.Rollups;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The two rules that decide how much work a pass takes on and where it reads it from. Both used to
/// be implicit and both were wrong in ways that cost prod: the pass had no upper bound, so arrears
/// it could not finish in one command timeout became arrears it could never finish; and every
/// timeframe was built from 1-minute bars, so a daily window meant re-reading 1,440 rows per
/// instrument. The aggregation arithmetic itself lives in <see cref="Rollup"/> and is tested there;
/// this is the pure half of the scheduling.
/// </summary>
public sealed class RollupJobTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cold_start_is_bounded_to_a_day_not_the_epoch()
    {
        var (since, _) = RollupJob.Window(watermark: null, StartedAt);

        Assert.Equal(StartedAt.AddDays(-1), since);
    }

    [Fact]
    public void Cold_start_still_only_takes_one_step_at_a_time()
    {
        // A day of arrears is a day of arrears whether it came from a fresh deploy or an outage.
        var (since, until) = RollupJob.Window(watermark: null, StartedAt);

        Assert.Equal(since.AddMinutes(30), until);
    }

    [Fact]
    public void A_current_watermark_is_looked_back_by_the_slack_and_runs_to_now()
    {
        var watermark = StartedAt.AddMinutes(-1);

        var (since, until) = RollupJob.Window(watermark, StartedAt);

        // 10 minutes of slack behind the watermark — wide enough that a 1m bar committed while the
        // previous pass was running still falls inside this pass's touched window.
        Assert.Equal(watermark.AddMinutes(-10), since);
        // Nothing to catch up on, so the slice ends at the present rather than a step short of it.
        Assert.Equal(StartedAt, until);
    }

    [Fact]
    public void Arrears_are_taken_a_step_at_a_time()
    {
        var watermark = StartedAt.AddHours(-4);

        var (since, until) = RollupJob.Window(watermark, StartedAt);

        Assert.Equal(watermark.AddMinutes(-10), since);
        Assert.Equal(watermark.AddMinutes(30), until);
    }

    [Fact]
    public void A_step_is_measured_from_the_watermark_so_slack_is_not_given_back()
    {
        // Measured from `since` instead, each pass would hand back the ten minutes of slack it had
        // just re-read and four hours would drain at 20 minutes an hour, not 30.
        var watermark = StartedAt.AddHours(-4);

        var (_, until) = RollupJob.Window(watermark, StartedAt);

        Assert.Equal(30, (until - watermark).TotalMinutes);
    }

    [Theory]
    [InlineData(5, 1)]        // nothing smaller is configured: the minute base
    [InlineData(15, 5)]
    [InlineData(60, 15)]
    [InlineData(240, 60)]
    [InlineData(720, 240)]
    [InlineData(1440, 720)]
    public void Each_timeframe_is_built_from_the_largest_divisor_below_it(int tf, int expected)
    {
        int[] configured = [5, 15, 60, 240, 720, 1440];

        Assert.Equal(expected, RollupJob.SourceFor(tf, configured));
    }

    [Fact]
    public void A_timeframe_nothing_divides_falls_back_to_the_minute_base()
    {
        // 7 is not a multiple of 5, so there is no cascade to ride and the minutes are the source.
        int[] configured = [5, 7];

        Assert.Equal(1, RollupJob.SourceFor(7, configured));
    }

    [Fact]
    public void A_gap_in_the_configured_ladder_is_bridged_not_broken()
    {
        // 60 is configured but 15 is not: 5 still divides 60, so the cascade uses 5 rather than
        // dropping all the way back to minutes.
        int[] configured = [5, 60];

        Assert.Equal(5, RollupJob.SourceFor(60, configured));
    }
}

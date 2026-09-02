using CryptoSmithX.MarketData.Hub.Rollups;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// Covers the watermark rule that used to be an in-memory field reset to the epoch on every
/// restart — the second root cause behind the 142s rollup pass, and the part <see cref="Rollup"/>'s
/// own tests do not touch. The aggregation itself now runs as SQL against a live database and is
/// verified there (see the rollup-scale task notes); this is the pure half of the fix.
/// </summary>
public sealed class RollupJobTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cold_start_is_bounded_to_a_day_not_the_epoch()
    {
        var since = RollupJob.SinceFor(lastSuccessAt: null, StartedAt);

        Assert.Equal(StartedAt.AddDays(-1), since);
    }

    [Fact]
    public void A_known_watermark_is_looked_back_by_the_slack()
    {
        var lastSuccess = StartedAt.AddMinutes(-1);

        var since = RollupJob.SinceFor(lastSuccess, StartedAt);

        // 10 minutes of slack behind the last successful pass — wide enough that a late-arriving
        // 1m bar still falls inside the next pass's touched window and repairs its parent.
        Assert.Equal(lastSuccess.AddMinutes(-10), since);
    }

    [Fact]
    public void A_stale_watermark_is_still_only_looked_back_by_the_slack_not_the_gap()
    {
        // The service was down for three days; last_success_at is old, but resuming from
        // (old watermark - slack) is exactly what "repair, don't restart from scratch" means —
        // it is a deliberate backfill's job to go further back than that.
        var lastSuccess = StartedAt.AddDays(-3);

        var since = RollupJob.SinceFor(lastSuccess, StartedAt);

        Assert.Equal(lastSuccess.AddMinutes(-10), since);
    }
}

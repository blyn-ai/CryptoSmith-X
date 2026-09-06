using CryptoSmithX.WebApp.Admin.Data;

namespace CryptoSmithX.WebApp.Admin.Tests;

/// <summary>
/// The dashboard's "degraded" line. It was a literal 30 s that happened to equal ws_stale_after_s,
/// which made it unsatisfiable for any venue served over WebSocket: a cached record is allowed to be
/// exactly that old when it is written. Kraken sat at 39 s in production and the whole venue read
/// degraded for obeying a setting we had given it.
/// </summary>
public sealed class StaleThresholdTests
{
    [Fact]
    public void The_threshold_clears_the_websocket_staleness_allowance()
    {
        // Anything at or below what a cached record may legitimately be is not evidence of a fault.
        Assert.True(DashboardStore.StaleThresholdSeconds(30, 10) > 30);
    }

    [Fact]
    public void Kraken_at_thirty_nine_seconds_is_not_degraded()
    {
        Assert.True(39 < DashboardStore.StaleThresholdSeconds(wsStaleAfterSeconds: 30, pollSeconds: 10));
    }

    [Fact]
    public void A_venue_silent_for_minutes_still_is()
    {
        Assert.True(300 > DashboardStore.StaleThresholdSeconds(30, 10));
    }

    [Theory]
    [InlineData(30, 10, 60)]
    [InlineData(30, 60, 210)]
    [InlineData(0, 10, 30)]
    public void Both_halves_come_from_configuration(int ws, int poll, double expected) =>
        Assert.Equal(expected, DashboardStore.StaleThresholdSeconds(ws, poll));

    [Fact]
    public void An_unknown_poll_interval_falls_back_to_the_snapshot_default()
    {
        Assert.Equal(DashboardStore.StaleThresholdSeconds(30, 10), DashboardStore.StaleThresholdSeconds(30, null));
    }
}

using CryptoSmithX.WebApp.Models;

namespace CryptoSmithX.WebApp.Tests;

/// <summary>
/// The cross-venue comparison. Two rules carry the whole page: the compared window must already have
/// CLOSED at the instant asked for, and the only claim made across venues must be one that no clock
/// difference can manufacture.
/// </summary>
public sealed class AssetAtInstantTests
{
    private static DateTime Utc(string s) => DateTime.SpecifyKind(DateTime.Parse(s), DateTimeKind.Utc);

    [Fact]
    public void The_window_containing_the_instant_is_never_the_one_compared()
    {
        // 18:02:30 sits inside the 18:02 bar, which closes at 18:03 — thirty seconds of trades that
        // had not happened yet. The comparison takes 18:01–18:02 instead.
        Assert.Equal(Utc("2026-09-01 18:01:00"), AssetAtInstant.Anchor(Utc("2026-09-01 18:02:30"), 1));
    }

    [Fact]
    public void An_instant_exactly_on_a_boundary_takes_the_window_that_just_closed()
    {
        Assert.Equal(Utc("2026-09-01 19:14:00"), AssetAtInstant.Anchor(Utc("2026-09-01 19:15:00"), 1));
    }

    [Theory]
    [InlineData(5, "2026-09-01 18:07:10", "2026-09-01 18:00:00")]
    [InlineData(15, "2026-09-01 18:07:10", "2026-09-01 17:45:00")]
    [InlineData(60, "2026-09-01 18:07:10", "2026-09-01 17:00:00")]
    [InlineData(240, "2026-09-01 18:07:10", "2026-09-01 12:00:00")]
    [InlineData(1440, "2026-09-01 18:07:10", "2026-08-31 00:00:00")]
    public void Longer_windows_stay_epoch_aligned(short tf, string at, string expected)
    {
        var anchor = AssetAtInstant.Anchor(Utc(at), tf);
        Assert.Equal(Utc(expected), anchor);
        Assert.True(anchor.AddMinutes(tf) <= Utc(at), "the compared window must have closed by the instant asked for");
    }

    [Fact]
    public void Disjoint_ranges_are_a_claim_no_clock_difference_can_manufacture()
    {
        // Both venues measured over the same sixty seconds. There is no price at which both traded.
        var slice = Slice(Bar("a", low: 100, high: 101, close: 100.5), Bar("b", low: 103, high: 104, close: 103.5));
        Assert.Equal(2.0, slice.DisjointGap!.Value, 6);
    }

    [Fact]
    public void Overlapping_ranges_assert_nothing_even_when_the_closes_differ()
    {
        // Closes 1.0 apart, but both venues traded at 101 during the window, so there was no
        // divergence to report - only two last-trades that landed at different moments.
        var slice = Slice(Bar("a", low: 100, high: 102, close: 100.5), Bar("b", low: 100.5, high: 102.5, close: 101.5));
        Assert.Null(slice.DisjointGap);
        Assert.NotNull(slice.CloseSpreadBps);
    }

    [Fact]
    public void A_bar_with_no_volume_never_enters_the_comparison()
    {
        var slice = Slice(Bar("a", 100, 101, 100.5), Bar("b", 200, 201, 200.5, volume: 0));
        Assert.Single(slice.Comparable);
        Assert.Null(slice.DisjointGap);
        Assert.Contains(slice.Excluded, e => e.Reason.Contains("no volume"));
    }

    [Fact]
    public void Venues_on_different_contract_terms_are_not_compared()
    {
        // USD against USDT is not the same number, and this system was never told a conversion.
        var slice = Slice(Bar("a", 100, 101, 100.5), Bar("b", 103, 104, 103.5, quote: "USDT"));
        Assert.Single(slice.Comparable);
        Assert.Null(slice.DisjointGap);
        Assert.Contains(slice.Excluded, e => e.Reason.Contains("USDT"));
    }

    [Fact]
    public void An_incomplete_bar_is_held_out_with_its_coverage_stated()
    {
        var slice = Slice(
            Bar("a", 100, 101, 100.5, timeframe: 5, barCount: 5),
            Bar("b", 103, 104, 103.5, timeframe: 5, barCount: 2));
        Assert.Single(slice.Comparable);
        Assert.Contains(slice.Excluded, e => e.Reason.Contains("2 of 5"));
    }

    private static AssetVenueBar Bar(
        string segment, double low, double high, double close,
        double volume = 10, string quote = "USD", short timeframe = 1, short barCount = 1) =>
        new(segment, $"{segment.ToUpperInvariant()}-X", quote, 1m,
            Utc("2026-09-01 18:01:00"), timeframe,
            Open: close, High: high, Low: low, Close: close,
            Volume: volume, TradeCount: null, BarCount: barCount);

    private static AssetAtInstant Slice(params AssetVenueBar[] bars) =>
        new("X", "X", Utc("2026-09-01 18:02:30"), bars[0].Timeframe, Utc("2026-09-01 18:01:00"),
            [], bars, [], null);
}

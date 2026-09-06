using CryptoSmithX.Arena.Data;
using CryptoSmithX.Arena.Models;

namespace CryptoSmithX.Arena.Tests;

/// <summary>
/// The fade, and the window it is measured against.
///
/// The second half matters more than the first. A wrong fade curve is ugly; a wrong window prints
/// △ over a healthy venue and then the word "degraded" over a collector doing exactly what it was
/// configured to do. That has already happened once in this repository — <c>StaleThresholdTests</c>
/// in the WebApp exists because a literal 30 s made Kraken read degraded at 39 s — and the public
/// page is a worse place to do it a second time.
/// </summary>
public sealed class FreshnessTests
{
    [Fact]
    public void A_figure_is_at_full_strength_when_its_call_lands()
    {
        Assert.Equal(1.0, Freshness.Weight(0, 30), 10);
    }

    [Fact]
    public void The_amplitude_is_derived_from_the_floor_and_not_written_down_beside_it()
    {
        // At the end of the window the figure sits exactly on the floor. If the amplitude were a
        // fourth constant, moving the floor would leave a step here.
        Assert.Equal(Freshness.Floor, Freshness.Weight(30, 30), 10);
    }

    [Fact]
    public void Past_the_window_nothing_is_graded_further()
    {
        // Thirty-one seconds and thirty days are the same verdict.
        Assert.Equal(Freshness.Weight(31, 30), Freshness.Weight(TimeSpan.FromDays(30).TotalSeconds, 30), 10);
    }

    [Fact]
    public void The_fade_is_front_loaded()
    {
        // A tenth of the window costs far more than a tenth of the amplitude — that is the whole
        // point of the exponent, and a linear ramp would pass every other test in this class.
        var linear = 1.0 - (1.0 - Freshness.Floor) * 0.1;
        Assert.True(Freshness.Weight(3, 30) < linear);
    }

    [Fact]
    public void A_venue_clock_running_ahead_of_ours_is_not_evidence_of_anything()
    {
        // received_at is the VENUE's clock on Kraken, not ours (SnapshotCollector), so a negative
        // age is a fact about two clocks and never a reason to brighten or fault a figure.
        Assert.Equal(1.0, Freshness.Weight(-5, 30), 10);
        Assert.False(Freshness.PastWindow(-5, 30));
    }

    [Fact]
    public void A_call_with_no_known_cadence_neither_fades_nor_carries_the_mark()
    {
        // Not knowing how often we look is not the same as knowing the figure is old. Inventing a
        // window here would be the page making up the one thing it exists to report.
        Assert.Equal(1.0, Freshness.Weight(900, null), 10);
        Assert.False(Freshness.PastWindow(900, null));
        Assert.False(Freshness.Degraded(900, null));
    }

    [Fact]
    public void Degraded_is_twelve_windows_and_not_a_second_earlier()
    {
        Assert.False(Freshness.Degraded(30 * 12 - 1, 30));
        Assert.True(Freshness.Degraded(30 * 12, 30));
    }

    [Fact]
    public void An_instant_we_do_not_have_has_no_age()
    {
        Assert.Null(Freshness.AgeSeconds(null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void The_age_is_measured_against_the_request_not_against_the_row()
    {
        var written = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var requested = new DateTimeOffset(written).AddSeconds(7);
        Assert.Equal(7, Freshness.AgeSeconds(written, requested)!.Value, 6);
    }

    // ── the window ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_weex_depth_sweep_does_not_read_as_a_fault()
    {
        // 0021 measured one WEEX depth pass at 361 s across 1,005 instruments, against the 60 s
        // depth interval 0014 seeds. Judged against a flat 30 s — or against the bare interval —
        // every healthy cell on that venue would carry △.
        var weex = new SegmentFreshness("weex-futures",
            SnapshotIntervalSeconds: 10, DepthIntervalSeconds: 60, OpenInterestIntervalSeconds: null,
            PricePassSeconds: 4, OpenInterestPassSeconds: 40, DepthPassSeconds: 361);

        var depth = weex.Windows.DepthSeconds;
        Assert.NotNull(depth);
        Assert.True(361 < depth, $"a measured 361 s pass must fit inside its own window, was {depth}");
        Assert.True(Freshness.PastWindow(361, 30), "the flat 30 s this replaces would have marked it");
    }

    [Fact]
    public void A_price_and_a_depth_band_on_one_row_are_judged_by_different_clocks()
    {
        // One row holding a two-second price and a two-minute depth sweep is the normal case, not
        // a fault. Collapsing the two windows into one is the bug this record exists to prevent.
        var w = new SegmentFreshness("weex-futures", 10, 60, null, 4, 40, 361).Windows;
        Assert.NotEqual(w.PriceSeconds, w.DepthSeconds);
        Assert.True(w.PriceSeconds < w.DepthSeconds);
    }

    [Fact]
    public void Kraken_at_thirty_nine_seconds_is_not_stale_when_the_segment_measures_that_wide()
    {
        // The incident, on the public page this time. received_at on Kraken is the venue's own
        // clock and a cached socket record may legitimately carry an instant tens of seconds old,
        // so the segment's own dispersion is what has to answer for it — not a literal.
        var kraken = new SegmentFreshness("kraken-futures", 10, 60, null,
            PricePassSeconds: 31, OpenInterestPassSeconds: 31, DepthPassSeconds: 14);

        Assert.False(Freshness.PastWindow(39, kraken.Windows.PriceSeconds));
    }

    [Fact]
    public void A_stalled_pass_cannot_stretch_the_window_without_limit()
    {
        // A venue that stops serving the book for more than one instrument in twenty drags the
        // percentile up. Without the cap the window would grow to swallow the outage and the page
        // would never call anything old again.
        var broken = new SegmentFreshness("weex-futures", 10, 60, null, 4, 40,
            DepthPassSeconds: TimeSpan.FromDays(2).TotalSeconds);

        Assert.Equal(60 + 60 * SegmentFreshness.PassCapWindows, broken.Windows.DepthSeconds);
        Assert.True(Freshness.Degraded(TimeSpan.FromDays(2).TotalSeconds, broken.Windows.DepthSeconds));
    }

    [Fact]
    public void A_segment_with_no_measured_pass_is_judged_by_its_cadence_alone()
    {
        // Nothing observed yet is not a reason to widen anything.
        var fresh = new SegmentFreshness("binance-usdm", 10, 60, null, null, null, null).Windows;
        Assert.Equal(10, fresh.PriceSeconds);
        Assert.Equal(60, fresh.DepthSeconds);
    }

    [Fact]
    public void Open_interest_rides_the_snapshot_clock_only_while_it_has_no_loop_of_its_own()
    {
        // 0014 disables the open_interest dataset everywhere with the note that it is carried inline
        // in the snapshot ticker. When a venue does run it separately, its own cadence takes over.
        var inline = new SegmentFreshness("weex-futures", 10, 60, null, 0, 0, 0).Windows;
        Assert.Equal(10, inline.OpenInterestSeconds);

        var separate = new SegmentFreshness("binance-usdm", 10, 60, 60, 0, 0, 0).Windows;
        Assert.Equal(60, separate.OpenInterestSeconds);
    }

    [Fact]
    public void A_segment_with_no_cadence_at_all_gets_no_window_rather_than_a_guess()
    {
        var unknown = new SegmentFreshness("nowhere", null, null, null, 5, 5, 5).Windows;
        Assert.Null(unknown.PriceSeconds);
        Assert.Null(unknown.DepthSeconds);
        Assert.Null(unknown.OpenInterestSeconds);
    }

    // ── The freshness strip: its labels are on the scale they sit under ──────────────────────────

    /// <summary>A row whose three calls carry three ages against three windows, which is the only
    /// shape any of this is about. <see cref="Rows.At"/> gives all three the same window on
    /// purpose — a test about ranking should not have to state a cadence — and under one window the
    /// defect below cannot be expressed at all.</summary>
    private static VenueRowModel Row(
        double? priceAge, double? priceWindow,
        double? depthAge, double? depthWindow,
        double? oiAge = null, double? oiWindow = null)
    {
        var now = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var row = Rows.Venue(1) with
        {
            ReceivedAt = priceAge is { } p ? now.AddSeconds(-p) : null,
            DepthAt = depthAge is { } d ? now.AddSeconds(-d) : null,
            OpenInterestAt = oiAge is { } o ? now.AddSeconds(-o) : null
        };

        return new VenueRowModel(
            row,
            new FreshnessWindows(priceWindow, oiWindow, depthWindow),
            new CallAges(priceAge, oiAge, depthAge),
            CandleSeries.Empty,
            MetricHourSeries.Empty);
    }

    [Fact]
    public void The_two_ends_of_the_scale_are_the_two_ends_of_the_scale()
    {
        // The ordinary WEEX configuration, and the reason this was never an edge case: a 10 s price
        // cadence beside a depth pass measured at 300 s. The price call is at 230% of its window and
        // the depth sweep at 12.7% of its own, so the price call is the spent end of the gradient —
        // and the strip printed "fresh 23 s" (the price) under the green end and "△ old 38 s" (the
        // depth sweep) in the hold ink under the magenta one. Both labels named the opposite end.
        var strip = StripModel.Build(Row(priceAge: 23, priceWindow: 10, depthAge: 38, depthWindow: 300));

        Assert.Equal("Depth", strip.LeastSpent!.Label);
        Assert.Equal("Price", strip.MostSpent!.Label);

        // And the raw ages, which is what the old selection returned, run the other way round.
        Assert.True(strip.MostSpent.AgeSeconds < strip.LeastSpent.AgeSeconds);
    }

    [Fact]
    public void The_triangle_belongs_to_the_call_at_the_end_it_is_printed_on()
    {
        // `calls.Any(c => c.PastWindow)` put the mark and the hold ink on the magenta label whatever
        // that label named. Reproduced on the running app: a depth sweep 92 s into a 300 s pass wore
        // the △ while the price call 62 s into a 10 s window, six windows past, wore nothing.
        var late = StripModel.Build(Row(priceAge: 62, priceWindow: 10, depthAge: 92, depthWindow: 300, oiAge: 5, oiWindow: 60));

        Assert.Equal("Price", late.MostSpent!.Label);
        Assert.True(late.MostSpentPastWindow);

        // …and a row where the end's own call is inside its window carries no mark, however wide the
        // spread of raw ages under it.
        var healthy = StripModel.Build(Row(priceAge: 3, priceWindow: 10, depthAge: 200, depthWindow: 300));

        Assert.Equal("Depth", healthy.MostSpent!.Label);
        Assert.False(healthy.MostSpentPastWindow);
    }

    [Fact]
    public void Two_calls_both_past_their_windows_are_ordered_by_how_far_past()
    {
        // The tick is clamped to the scale because it cannot be drawn off the end of one; the END is
        // chosen unclamped, or two spent calls tie at 1.0 and the answer is whichever the row
        // enumerated first — an ordering the data does not have.
        var strip = StripModel.Build(Row(priceAge: 40, priceWindow: 10, depthAge: 3_000, depthWindow: 300));

        Assert.Equal("Depth", strip.MostSpent!.Label);   // 10 windows
        Assert.Equal("Price", strip.LeastSpent!.Label);  // 4 windows
        Assert.Equal(1.0, strip.MostSpent.Position, 10);
        Assert.Equal(1.0, strip.LeastSpent.Position, 10);
    }

    [Fact]
    public void A_call_with_no_stated_cadence_is_at_neither_end_of_a_scale_it_is_not_on()
    {
        // It has no tick for the same reason. Its age is in the named list under the scale, which is
        // where an observation nobody can grade belongs — putting it at the magenta end would read
        // as "very old", which is a measurement we do not have.
        var strip = StripModel.Build(Row(priceAge: 5, priceWindow: 10, depthAge: 9_000, depthWindow: null));

        Assert.Equal(2, strip.Calls.Count); // price and depth landed; the OI call never has
        Assert.Equal("Price", strip.LeastSpent!.Label);
        Assert.Equal("Price", strip.MostSpent!.Label);
        Assert.Contains(strip.Calls, c => c.Label == "Depth" && !c.Placed);
    }

    [Fact]
    public void A_row_no_call_on_which_states_a_cadence_has_no_ends_rather_than_invented_ones()
    {
        var strip = StripModel.Build(Row(priceAge: 5, priceWindow: null, depthAge: 900, depthWindow: null));

        Assert.Null(strip.LeastSpent);
        Assert.Null(strip.MostSpent);
        Assert.Equal("", StripModel.EndText(strip.LeastSpent));
        Assert.False(strip.MostSpentPastWindow);
    }

    [Fact]
    public void An_end_label_names_its_call_and_its_hover_says_what_the_age_is_a_share_of()
    {
        var strip = StripModel.Build(Row(priceAge: 23, priceWindow: 10, depthAge: 38, depthWindow: 300));

        Assert.Equal("Depth 38 s", StripModel.EndText(strip.LeastSpent));
        Assert.Equal("Price 23 s", StripModel.EndText(strip.MostSpent));
        Assert.Equal(
            "Least spent: depth, 38 s into its 300 s window",
            StripModel.EndTitle(strip.LeastSpent, StripModel.LeastSpentLabel));
        Assert.Equal(
            "Most spent: price, 23 s into its 10 s window",
            StripModel.EndTitle(strip.MostSpent, StripModel.MostSpentLabel));
    }

    [Fact]
    public void The_client_picks_the_two_ends_the_same_way_and_says_the_same_words()
    {
        // The strip is the one part of this page BOTH halves draw — the server for the first paint,
        // arena-ages.js every second after it — so the fix had to land in both or the labels would
        // invert again one second after load. Same mechanism as the statement line: the words are
        // constants on the server and this reads the script for them.
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "arena-ages.js"));

        Assert.Contains("'" + StripModel.LeastSpentLabel + "'", script, StringComparison.Ordinal);
        Assert.Contains("'" + StripModel.MostSpentLabel + "'", script, StringComparison.Ordinal);
        Assert.Contains("' into its '", script, StringComparison.Ordinal);
        Assert.Contains("' s window'", script, StringComparison.Ordinal);

        // The selection itself: a share of the call's own window, and nothing that reaches for the
        // raw minimum or maximum age of the row.
        Assert.Contains("ageS / call.win", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'fresh '", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'old '", script, StringComparison.Ordinal);
    }
}

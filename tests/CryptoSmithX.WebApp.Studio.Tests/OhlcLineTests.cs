using CryptoSmithX.WebApp.Studio;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The line above a candle panel: what it says, and what it reserves room for.
///
/// <b>Why these are worth a file.</b> The line is six fields rewritten under a moving pointer, so
/// two things can go wrong that nothing else in this suite would notice. It can state a figure
/// wrongly — a change against the wrong base, a zero where nothing was measured — and it can state
/// it correctly in a slot too small, which is the venue cell's defect on a new surface: a value
/// that gains a character moves every key to its right, every time the pointer crosses a bar.
///
/// The second half is the reason the counts live in <see cref="OhlcLine"/> rather than in the view.
/// A slot is a promise about the longest string a panel can print, and a promise nothing can check
/// is a preference.
/// </summary>
public sealed class OhlcLineTests
{
    private static readonly DateTime[] Hours =
        [.. Enumerable.Range(0, 4).Select(i => new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc).AddHours(i))];

    private static CandleRow Bar(int i, double open, double high, double low, double close) =>
        new(1, Hours[i], open, high, low, close, 60, "rest", Hours[i]);

    private static CandleSeries Series(params CandleRow?[] bars) => new(Hours, bars);

    // ── What it says ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rule 1 on a rolled-up figure: the four numbers are dated by the hour they cover, and the
    /// date is the window's, in UTC, like every other instant on the surface.
    /// </summary>
    [Fact]
    public void The_line_rests_on_the_last_closed_bar_and_names_its_hour()
    {
        var line = OhlcLine.Build(
            Series(Bar(0, 10, 12, 9, 11), Bar(1, 11, 14, 11, 13), null, null), decimals: 2);

        Assert.Equal("07 01:00 UTC", line.When);
        Assert.Equal("11.00", line.Open);
        Assert.Equal("14.00", line.High);
        Assert.Equal("11.00", line.Low);
        Assert.Equal("13.00", line.Close);
    }

    /// <summary>
    /// THE CHANGE IS THE BAR'S OWN CLOSE AGAINST ITS OWN OPEN. Not against the previous close, not
    /// against the first bar on the panel, not against twenty-four hours ago — and the line prints
    /// <see cref="OhlcLine.ChangeLabel"/> beside it rather than leaving the reader to work out which
    /// of those four this page picked.
    ///
    /// The bar chosen here would read +30.00% against the previous close and does not.
    /// </summary>
    [Fact]
    public void The_change_is_that_bar_s_close_against_that_bar_s_own_open()
    {
        var line = OhlcLine.Build(Series(Bar(0, 10, 12, 9, 10), Bar(1, 12, 14, 11, 13), null, null), 2);

        Assert.Equal("+8.33%", line.Change);
        Assert.Equal("CLOSE VS OPEN", OhlcLine.ChangeLabel);
    }

    /// <summary>The sign is the only thing carrying direction — the change takes no ink (rule 5,
    /// and RULE-CHANGES entry 10 scopes the fill exception to candle bodies) — so it is always
    /// printed.</summary>
    [Fact]
    public void A_fall_states_its_sign()
    {
        Assert.Equal("-10.00%", OhlcLine.ChangeOf(Bar(0, 10, 10, 8, 9)));
    }

    /// <summary>
    /// Rule 8, on both sides of it. An hour with no bar is a dash: not measured. An hour that
    /// opened and closed at the same price is <c>0.00%</c>: measured, and it did not move. The
    /// dash-for-a-flat-hour version of this would say we do not know what a venue we watched all
    /// hour did.
    /// </summary>
    [Fact]
    public void A_missing_hour_is_a_dash_and_a_flat_hour_is_a_zero()
    {
        Assert.Equal(Format.Dash, OhlcLine.ChangeOf(null));
        Assert.Equal("0.00%", OhlcLine.ChangeOf(Bar(0, 10, 11, 9, 10)));
    }

    /// <summary>
    /// An open of zero divides to an infinity, and the em dash is what the surface prints for a
    /// figure it does not have. There is deliberately no second guard for it in
    /// <see cref="OhlcLine.ChangeOf"/>: one rule for "not measured", written once, in
    /// <see cref="Format.SignedPercent"/>.
    /// </summary>
    [Fact]
    public void An_open_of_zero_is_a_dash_rather_than_an_infinity()
    {
        Assert.Equal(Format.Dash, OhlcLine.ChangeOf(Bar(0, 0, 1, 0, 1)));
        Assert.Equal(Format.Dash, OhlcLine.ChangeOf(Bar(0, 0, 0, 0, 0)));
    }

    /// <summary>A panel drawn from nothing states nothing, and states it as dashes. Unreachable
    /// through the view — a panel needs two closed bars to be drawn at all — and asserted anyway,
    /// because the alternative to a dash in an empty series is an exception on a public page.</summary>
    [Fact]
    public void A_series_with_no_bars_is_dashes_and_a_slot_one_character_wide()
    {
        var line = OhlcLine.Build(Series(null, null, null, null), 2);

        Assert.Equal(Format.Dash, line.When);
        Assert.Equal(Format.Dash, line.Open);
        Assert.Equal(Format.Dash, line.Change);
        Assert.Equal(1, line.FigureGlyphs);
        Assert.Equal(1, line.ChangeGlyphs);
    }

    // ── What it reserves ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FIGURE SLOT HOLDS EVERY FIGURE THIS PANEL CAN PUT IN IT — all four of them, on every
    /// bar, not just the bar the line happens to rest on.
    ///
    /// This is the assertion the whole model exists for. The crosshair can reach any bar in the
    /// series, so the slot is a claim about the series and not about the last hour of it; a slot
    /// counted from the resting bar alone would be correct on load and wrong the first time the
    /// pointer crossed a longer figure, which is the failure mode that looks like the page is fine.
    /// </summary>
    [Fact]
    public void The_figure_slot_holds_every_figure_in_the_series_and_not_only_the_last()
    {
        // The long figure is in the FIRST hour and the line rests on the last: 9,876.54 is eight
        // characters against the resting bar's five.
        var series = Series(
            Bar(0, 9876.54, 9900.00, 9800.00, 9850.00),
            Bar(1, 12.00, 13.00, 11.00, 12.50),
            null,
            Bar(3, 12.50, 12.75, 12.25, 12.60));

        var line = OhlcLine.Build(series, decimals: 2);

        Assert.Equal(8, line.FigureGlyphs);

        foreach (var bar in series.Bars.Where(b => b is not null))
        {
            foreach (var figure in new[] { bar!.Open, bar.High, bar.Low, bar.Close })
            {
                Assert.True(Format.Num(figure, 2).Length <= line.FigureGlyphs,
                    $"`{Format.Num(figure, 2)}` does not fit a slot of {line.FigureGlyphs} characters");
            }
        }
    }

    /// <summary>
    /// THE CHANGE SLOT IS COUNTED TOO, and this is the case that killed the literal it replaced.
    ///
    /// The slot was <c>9ch</c> — a hand-picked maximum, "a sign, three digits, a point, two decimals
    /// and the per-cent" — which is exactly the shape of fix the owner rejected when he rejected
    /// sizing the venue cell to its worst case. It is also wrong: an hour in which a token trebles
    /// prints <c>+1,204.55%</c>, which is ten characters, and on this page the token is PEPE.
    /// </summary>
    [Fact]
    public void The_change_slot_holds_a_move_no_hand_picked_width_would_have()
    {
        var series = Series(
            Bar(0, 1.00, 14.00, 1.00, 13.04),   // +1,204.00%: ten characters
            Bar(1, 13.04, 13.10, 12.90, 13.00),
            null,
            Bar(3, 13.00, 13.20, 12.80, 13.10));

        var line = OhlcLine.Build(series, decimals: 2);

        Assert.Equal("+1,204.00%", OhlcLine.ChangeOf(series.Bars[0]));
        Assert.Equal(10, line.ChangeGlyphs);

        foreach (var bar in series.Bars)
        {
            Assert.True(OhlcLine.ChangeOf(bar).Length <= line.ChangeGlyphs,
                $"`{OhlcLine.ChangeOf(bar)}` does not fit a slot of {line.ChangeGlyphs} characters");
        }
    }

    /// <summary>
    /// Both slots hold the em dash whether or not this panel has a gap of its own — and on a panel
    /// of one-character prices the dash is the longest thing the figure slot holds.
    ///
    /// It matters because the crosshair sweeps the page's SHARED window list: a panel with no gaps
    /// can still be asked for an hour it has no bar for, which is the hour its neighbour is missing,
    /// and it answers with dashes while the others state their figures. A count taken over present
    /// bars alone would be a slot that fits everything except the answer to that question.
    /// </summary>
    [Fact]
    public void The_dash_is_counted_even_on_a_panel_with_no_gaps_of_its_own()
    {
        var line = OhlcLine.Build(
            Series(Bar(0, 1, 1, 1, 1), Bar(1, 1, 1, 1, 1), Bar(2, 1, 1, 1, 1), Bar(3, 1, 1, 1, 1)),
            decimals: 0);

        Assert.Equal(Format.Dash.Length, line.FigureGlyphs);
        Assert.Equal("0.00%".Length, line.ChangeGlyphs);
    }

    /// <summary>
    /// The slot is the PANEL's, and the panels are counted apart. A venue quoting to eight decimals
    /// beside one quoting to two is the ordinary case on this page — PEPE/USD holds both — and one
    /// count for the whole page would print eleven characters of leading space in every panel that
    /// is not the widest.
    /// </summary>
    [Fact]
    public void Two_panels_at_two_ticks_get_two_counts()
    {
        // The open is the tick the live page quotes Kraken's PF_PEPEUSD at, and the one Format.Decimals
        // used to print as `0`.
        var fine = OhlcLine.Build(Series(Bar(0, 0.0000036, 0.0000037, 0.0000035, 0.0000036209),
            Bar(1, 0.0000036209, 0.0000038, 0.0000036, 0.0000037), null, null), decimals: 10);

        var coarse = OhlcLine.Build(Series(Bar(0, 10, 12, 9, 11), Bar(1, 11, 14, 11, 13), null, null),
            decimals: 2);

        Assert.True(fine.FigureGlyphs > coarse.FigureGlyphs);
        Assert.Equal("0.0000036209", fine.Open);
        Assert.Equal(12, fine.FigureGlyphs);
    }

    /// <summary>
    /// Two bars a day apart must not print the same label. A panel holds twenty-five hourly
    /// windows, so windows[0] and windows[24] are the same clock hour twenty-four hours apart:
    /// with an "HH:mm UTC" label a reader hovering the left edge of the chart is told a time that
    /// is equally true of the right edge, and nothing on screen says which one they are looking at.
    ///
    /// This is the test the format change exists for. It fails on "HH:mm UTC" and passes on
    /// "dd HH:mm UTC", and it is written against the two ends of a real panel rather than against
    /// two arbitrary instants, because twenty-five windows is exactly what makes the collision
    /// reachable — twenty-four would not.
    /// </summary>
    [Fact]
    public void Two_windows_a_day_apart_do_not_name_themselves_identically()
    {
        var first = new DateTime(2026, 9, 6, 7, 0, 0, DateTimeKind.Utc);
        var last = first.AddHours(24);

        Assert.NotEqual(OhlcLine.Hour(first), OhlcLine.Hour(last));
        Assert.Equal("06 07:00 UTC", OhlcLine.Hour(first));
        Assert.Equal("07 07:00 UTC", OhlcLine.Hour(last));

        // Constant width in both, which is what lets the slot in studio.css be exact.
        Assert.Equal(OhlcLine.Hour(first).Length, OhlcLine.Hour(last).Length);
        Assert.Equal(12, OhlcLine.Hour(first).Length);
    }
}

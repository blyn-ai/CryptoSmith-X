using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The design system's rules, asserted against the two files that either keep them or do not:
/// <c>studio.css</c> and the table that emits into it.
///
/// <b>Why the text and not a behaviour.</b> Nothing here is reachable from a method call. "Every
/// history slot is the same height", "a number is never wrapped in a Tag" and "no rule is defined
/// that nothing emits" are properties of a stylesheet, and every one of them was broken in a tree
/// that built clean under TreatWarningsAsErrors with all four test projects green. A rule that
/// nothing can fail is a preference; these are rules, so something has to be able to fail.
///
/// Every assertion below was measured in a browser first, at 1920, against a rendered four-venue
/// BTC row. The numbers are in the comments and in <c>studio.css</c>'s own; the tests guard the
/// declarations those numbers came out of.
/// </summary>
public sealed class DesignSystemTests
{
    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    /// <summary>
    /// One CSS rule's declaration block, by its WHOLE selector list.
    ///
    /// The list has to match exactly rather than by prefix, and that is not fussiness: the fade in
    /// this stylesheet is a grouped rule whose last selector is <c>.a-mirror</c> on its own line, so
    /// a looser match reads `opacity: var(--w, 1)` and reports that the mirror declares no height —
    /// which it then did not, and the test passed for the wrong reason. Exactness also keeps
    /// <c>.a-bar</c> off <c>.a-bar > i</c> and <c>.a-cell</c> off <c>.a-cell--depth</c>.
    /// </summary>
    private static string Block(string css, string selector)
    {
        var rule = Regex.Matches(StripComments(css), @"(?m)^([^{}@][^{}]*?)\{([^}]*)\}")
            .FirstOrDefault(m => string.Equals(
                Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim(), selector, StringComparison.Ordinal));

        Assert.True(rule is not null, $"studio.css has no rule whose selector is exactly `{selector}`");
        return rule!.Groups[2].Value;
    }

    // ── One line across a row ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The measured failure: <c>.a-cell</c> WAS <c>justify-content: center</c> inside a grid row
    /// stretched to its tallest cell, so a stack eight pixels short did not sit high — it sat four
    /// pixels LOW. With the bar at --bar-h and the mirror at --mirror-h, one row put its figures on
    /// three lines (104.1 / 108.1 / 107.6px) and its ages on three (137.1 / 133.1 / 136.9px).
    ///
    /// The fix is the distinction the design system already makes and this file had lost: --spark-h
    /// is the SLOT MetricCell reserves, --bar-h and --mirror-h are how thick the ink is inside it.
    /// After it, all four history slots measure 11px and every unwrapped figure in the row sits at
    /// 104.1px with its age at 137.1px.
    /// </summary>
    [Fact]
    public void There_is_one_history_slot_and_it_is_always_the_same_height()
    {
        var css = Source("studio.css");

        // The slot itself, reserved whatever is or is not inside it.
        Assert.Matches(@"height:\s*var\(--spark-h\)", Block(css, ".a-hist"));

        // And it is the ONLY thing that reserves it. Four sibling elements with four heights is
        // what made the old failure possible; one element with one height is why it cannot come
        // back. `.a-nospark` was the fourth of those and is gone with them.
        //
        // Comments stripped, on the convention phase two set: the argument for a rule has to be
        // free to name what it replaced, and "which is why `.a-nospark` is gone" is the most useful
        // sentence in that block.
        Assert.DoesNotContain("a-nospark", StripComments(css), StringComparison.Ordinal);
        Assert.DoesNotContain("a-nospark", Source("_PairTable.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_marks_inside_it_keep_the_thickness_the_design_system_gives_them()
    {
        // The slot and the ink are two numbers, and widening the first must not widen the second:
        // measured after the fix, the bar's track and fill are 3px inside an 11px slot and each
        // half of the mirror is 4px inside one.
        var css = Source("studio.css");

        Assert.Matches(@"height:\s*var\(--bar-h\)", Block(css, ".a-bar"));
        Assert.Matches(@"height:\s*var\(--mirror-h\)", Block(css, ".a-mirror"));
    }

    [Fact]
    public void Every_history_mark_the_table_draws_is_inside_that_slot()
    {
        // A stylesheet that reserves one slot and a view that emits a bar beside it rather than in
        // it is the old bug with a new shape, and nothing else here would notice: the CSS would
        // still be right, the page would still render, and the figures would be back on two lines.
        var view = Source("_PairTable.cshtml");

        var open = view.IndexOf("<span class=\"a-hist\">", StringComparison.Ordinal);
        Assert.True(open > 0, "the table emits no history slot");

        var close = view.IndexOf("</span>\n", view.IndexOf("class=\"a-bar\"", StringComparison.Ordinal), StringComparison.Ordinal);

        foreach (var mark in new[] { "class=\"a-spark\"", "class=\"a-mirror\"", "class=\"a-bar\"" })
        {
            var at = view.IndexOf(mark, StringComparison.Ordinal);
            Assert.True(at > open && at < close, $"{mark} is emitted outside the history slot");
        }
    }

    /// <summary>
    /// Rule 11's one "both" column: depth 25bps carries a line for the hour AND the mirrored bar
    /// for the two sides. They share the one slot instead of taking two, which is the only
    /// arrangement that satisfies rule 11 and keeps the row's single figure line at the same time —
    /// the rendered ui_kit stacks two full-height marks there and its depth 25bps figure sits 4.5px
    /// above the rest of its row.
    ///
    /// The path is computed at the same height the viewBox is written with. That pairing is the
    /// thing that has to hold: an SVG whose viewBox disagrees with its box does not clip and does
    /// not overflow, it silently rescales, and a line drawn at the wrong scale still looks like a
    /// line.
    /// </summary>
    [Fact]
    public void The_column_that_carries_both_marks_fits_them_in_one_slot()
    {
        Assert.Equal(CryptoSmithX.WebApp.Studio.Format.SparkHeight - CryptoSmithX.WebApp.Studio.Format.MirrorHeight,
            CryptoSmithX.WebApp.Studio.Format.SplitSparkHeight);

        var view = Source("_PairTable.cshtml");
        Assert.Contains("Format.SplitSparkHeight", view, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 @SparkW @SparkH(c)\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("viewBox=\"0 0 60 11\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_depth_separator_is_on_the_same_line_as_the_figures_beside_it()
    {
        // 12px type at `normal` is a 15.6px line box, and one of those inside `.a-pair` made the
        // three depth columns' figure row 3.6px taller than every other cell's — the third of the
        // three figure lines. Every figure on this surface is at line-height 1; so is its
        // punctuation.
        Assert.Matches(@"line-height:\s*1;", Block(Source("studio.css"), ".a-pair-sep"));
    }

    // ── A figure never leaves its column ────────────────────────────────────────────────────────

    /// <summary>
    /// The measured failure: a BTC book of ten million a side is 10 + 10 characters of DM Mono at
    /// 12px plus the separator and its gaps — 159.2px in a 148px content box. The pair hung 5.2px
    /// into the neighbouring depth column and stopped 0.8px short of its last digit.
    ///
    /// <c>max-width</c> is the half that contains it: this is a flex item in a column box at
    /// <c>align-items: flex-end</c>, which sizes to its content and overflows without one.
    /// <c>flex-wrap</c> is the half that decides what a pair that cannot fit does instead. After
    /// both, the pair measures exactly 148px — the content box — and wraps to a second line.
    /// </summary>
    [Fact]
    public void A_depth_pair_too_wide_for_its_column_wraps_inside_it_rather_than_over_its_neighbour()
    {
        var block = Block(Source("studio.css"), ".a-pair");

        Assert.Matches(@"max-width:\s*100%", block);
        Assert.Matches(@"flex-wrap:\s*wrap", block);

        // Right-aligned on both lines, or the second line breaks the tabular stack the column is
        // read down.
        Assert.Matches(@"justify-content:\s*flex-end", block);
    }

    /// <summary>
    /// A figure is never broken across two lines.
    ///
    /// This test used to assert the opposite — <c>overflow-wrap: anywhere</c> on <c>.a-fig</c>, with
    /// <c>white-space: nowrap</c> asserted ABSENT — and the deployed page it described printed eleven
    /// figures on PEPE/USD as "6,521,172," above "000". The mechanism it was written for is real (a
    /// content-sized flex item at <c>align-items: flex-end</c> hangs LEFT over the previous column's
    /// figure, and migration 0023's multipliers make nineteen-character open interest ordinary); the
    /// answer was not. A number split across two lines is not a smaller number, it is an unreadable
    /// one, and Num.jsx — the design system component this class implements — sets nowrap on every
    /// figure on the surface.
    /// </summary>
    [Fact]
    public void A_figure_is_never_broken_across_two_lines()
    {
        var fig = Block(Source("studio.css"), ".a-fig");

        Assert.Matches(@"white-space:\s*nowrap", fig);

        // The two ways of breaking a number, named so neither comes back: `anywhere` breaks between
        // any two characters, `break-word` breaks the same way one moment later.
        Assert.DoesNotMatch(@"overflow-wrap:\s*(anywhere|break-word)", fig);
        Assert.DoesNotMatch(@"word-break:\s*break-all", fig);

        // Kept, and for a different job than it used to have: it is what lets a `.a-fig` shrink
        // inside `.a-pair` so the pair can break BETWEEN its two figures.
        Assert.Matches(@"max-width:\s*100%", fig);
    }

    /// <summary>
    /// A figure too wide for its column is fitted to the column instead — and the fit is the
    /// column's, not the cell's.
    ///
    /// The server counts characters (<c>RowCells.FigureGlyphs</c>) and the sheet turns the count
    /// into a size against <c>100cqw</c>, because the seventeen track widths live in <c>--a-cols</c>
    /// and a second copy of them anywhere is a copy free to drift. Delete the container declaration
    /// and <c>100cqw</c> resolves against the viewport instead of the cell: every figure on the page
    /// silently takes the wrong size, and nothing else fails.
    /// </summary>
    [Fact]
    public void A_figure_wider_than_its_column_is_fitted_to_the_column_and_not_hidden()
    {
        var css = Source("studio.css");
        var cell = Block(css, ".a-cell");
        var fig = Block(css, ".a-fig");

        Assert.Matches(@"container-type:\s*inline-size", cell);
        Assert.Matches(@"--a-adv:", cell);

        // The fit itself, and the plain declaration under it that an engine without container-query
        // units falls back to. Two `font-size` lines, in that order.
        var sizes = Regex.Matches(fig, @"font-size:([^;]+);").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, sizes.Count);
        Assert.Matches(@"^\s*var\(--fs-data\)\s*$", sizes[0]);
        Assert.Contains("100cqw", sizes[1], StringComparison.Ordinal);
        Assert.Contains("--fig-n", sizes[1], StringComparison.Ordinal);

        // Never larger than the figure size the type ladder names: the fit only ever shrinks.
        Assert.Contains("min(var(--fs-data)", sizes[1].Replace(" ", ""), StringComparison.Ordinal);

        // The count is per column and the same on every row of it, so the view must read it from
        // the column index rather than from the cell it is drawing.
        Assert.Contains("--fig-n:@figureGlyphs[column]", Source("_PairTable.cshtml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A shrunken figure does not take the history and the age under it up by the pixels it saved.
    ///
    /// <c>line-height: 1</c> would: a 7.7px figure in a 7.7px line box moves everything below it 4px
    /// up, in one column, down the whole table — the "three figure lines and three age lines per
    /// row" this file removed, re-created by the fix for something else. The line box is a length,
    /// so it is one figure tall whatever the figure inside it is.
    /// </summary>
    [Fact]
    public void The_figure_slot_is_one_figure_tall_whatever_size_the_figure_is()
    {
        Assert.Matches(@"line-height:\s*var\(--fs-data\)", Block(Source("studio.css"), ".a-fig"));
    }

    /// <summary>
    /// And the figures in it sit on one baseline, which the fixed line box does not give on its own.
    ///
    /// A glyph box is centred in its line box by half-leading, so holding the line box at 12px while
    /// the font shrinks to fit moves the BASELINE up: measured in a browser over 5- to 19-character
    /// figures in an 84px cell, baselines at 10.0 / 10.0 / 9.5 / 9.0 / 8.0px below the box top,
    /// against age lines that all stayed at 12.0. Three numbers on one row floating two pixels above
    /// the rest, on a surface whose argument is that a row is one venue's single statement.
    ///
    /// The strut is a zero-width space at the ROW's size: it puts the line box's baseline where a
    /// full-size figure would put it, and the fitted text — always smaller, never larger — aligns to
    /// that. Its height comes from the font rather than from a literal, so it holds for the three
    /// fallbacks --font-mono names. <c>height</c> is the other half: the strut alone lets the box
    /// grow with the fitted line's own descent (12 → 14px at 19 characters, measured), which would
    /// push the history and the age back down and re-open the defect the line box was pinned to
    /// close. Measured after: baseline 10.0px and box 12.0px at every one of those five sizes.
    /// </summary>
    [Fact]
    public void A_fitted_figure_sits_on_the_same_baseline_as_the_rest_of_its_row()
    {
        var css = Source("studio.css");
        var strut = Block(css, ".a-fig::before");
        var fig = Block(css, ".a-fig");

        Assert.Matches(@"content:\s*""\\200B""", strut);
        Assert.Matches(@"font-size:\s*var\(--fs-data\)", strut);
        Assert.Matches(@"height:\s*var\(--fs-data\)", fig);

        // The strut is the row's size and never the fitted one: sized by --fig-n it would shrink
        // with the text it exists to hold still, and pin nothing.
        Assert.DoesNotContain("--fig-n", strut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_depth_pair_still_breaks_between_its_two_figures_before_either_one_breaks()
    {
        // The order matters and it is the whole reason the last-resort break is admissible. A flex
        // line is broken BETWEEN items before any item's own text is, so the ask moves whole
        // whenever it fits alone — which is every case the pair wrap was written for. Delete
        // `flex-wrap` and the two figures stop having a break of their own to take first.
        var pair = Block(Source("studio.css"), ".a-pair");

        Assert.Matches(@"flex-wrap:\s*wrap", pair);
        Assert.Matches(@"max-width:\s*100%", pair);
    }

    [Fact]
    public void The_row_keeps_one_figure_line_even_where_a_cell_wraps()
    {
        // `justify-content: center` inside a grid row stretched to its tallest cell moves a taller
        // cell's figure line UP by half a line while every other cell stays put: measured at 1920,
        // `.a-fig` tops at 104.1px in twelve columns and 96.1px in the two wrapped depth cells. The
        // phase that introduced the wrap called this unavoidable — "the three constraints cannot all
        // hold" — and it is the centring rather than the wrap that breaks the third.
        Assert.Matches(@"justify-content:\s*flex-start", Block(Source("studio.css"), ".a-cell"));
    }

    [Fact]
    public void The_figure_is_never_clipped_and_never_shortened_to_fit()
    {
        var css = Source("studio.css");

        // The two ways of "fixing" an overflow that hide a digit. Both are the dash-vs-zero lie in
        // graphic form: the page silently showing a number other than the one it measured.
        Assert.DoesNotMatch(@"(?m)^\.a-cell--depth\s*\{[^}]*overflow:\s*hidden", css);
        Assert.DoesNotMatch(@"(?m)^\.a-pair\s*\{[^}]*text-overflow", css);
        Assert.DoesNotMatch(@"(?m)^\.a-fig\s*\{[^}]*text-overflow", css);
    }

    // ── A number is never wrapped in a Tag ──────────────────────────────────────────────────────

    [Fact]
    public void The_funding_note_is_not_a_Tag()
    {
        // Tag.prompt.md forbids it in five words — "Never wrap a number in a Tag" — and the port
        // proved the rule right by having to strip the .17em tracking and add tabular figures
        // before the thing was legible, i.e. undo the two properties that make a Tag a Tag. It
        // shared a class with SPOT and PERP, so restyling `.a-tag` from the system's own definition
        // put tracked non-tabular digits back on it and pushed it past the 100px column.
        var css = Source("studio.css");
        var view = Source("_PairTable.cshtml");

        Assert.DoesNotContain("a-tag--note", css, StringComparison.Ordinal);
        Assert.DoesNotContain("a-tag--note", view, StringComparison.Ordinal);

        Assert.Contains("class=\"a-mark-note\"", view, StringComparison.Ordinal);

        // Its own class, and not a Tag wearing a different name: it must not be emitted alongside
        // `a-tag` either.
        Assert.DoesNotMatch(@"a-tag[^""]*a-mark-note|a-mark-note[^""]*a-tag", view);

        var block = Block(css, ".a-mark-note");
        Assert.Matches(@"font-variant-numeric:\s*tabular-nums", block);
        Assert.Matches(@"letter-spacing:\s*0;", block);

        // Rule 9 gives a ground to BEST and WORST and to nothing else, and this is not a rank.
        Assert.DoesNotMatch(@"background", block);
    }

    // ── No rule is defined that nothing emits ───────────────────────────────────────────────────

    /// <summary>
    /// The generalisation of the three dead rules that were found by grepping: <c>.a-tag--wide</c>,
    /// <c>.a-tag--alarm</c> and <c>.a-bar--oi</c>. Naming those three in a test would have caught
    /// those three; this catches the next one.
    ///
    /// The danger is not the wasted bytes. <c>.a-tag--wide</c> set an ink and no ground, so the next
    /// person to reach for the obviously-named class for the spread's WIDE mark would have got bare
    /// coloured type where the design system and the kit both put a washed WORST chip — rule 7
    /// broken by a class named after the thing it breaks. A tone with no user is a trap with a
    /// helpful name.
    /// </summary>
    [Fact]
    public void Every_class_studio_css_defines_is_emitted_by_something()
    {
        var css = Source("studio.css");
        var emitters = new[]
        {
            "_PairTable.cshtml", "_Statement.cshtml", "_Layout.cshtml", "_Stamps.cshtml",
            "Pair.cshtml", "Index.cshtml", "PairNotFound.cshtml",
            "_VenueDetail.cshtml", "_AssetRegistry.cshtml",
            "studio-ages.js", "studio-live.js", "studio-candles.js", "studio-disclose.js"
        };
        var emitted = string.Concat(emitters.Select(Source));

        // Selectors only: a class name inside a comment does not count as a definition, and the
        // comments in this file quote class names constantly — deliberately, since half of them
        // explain why a class is NOT there.
        var declared = Regex.Matches(StripComments(css), @"(?m)^[^{}/@][^{}]*\{")
            .SelectMany(m => Regex.Matches(m.Value, @"\.(a-[a-z0-9-]+)").Select(c => c.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(declared);

        var orphans = declared
            .Where(c => !Regex.IsMatch(emitted, @"(?<![a-z0-9-])" + Regex.Escape(c) + @"(?![a-z0-9-])"))
            .ToList();

        Assert.True(orphans.Count == 0, "studio.css defines rules nothing emits: " + string.Join(", ", orphans));
    }

    [Fact]
    public void Every_class_the_views_emit_is_defined()
    {
        // The other direction, and the one that renders as an unstyled cell rather than as nothing:
        // a view that emits `a-mark-note` against a stylesheet that still says `a-tag--note` puts
        // tracked, non-tabular, full-strength digits in the mark slot of four cells.
        var css = StripComments(Source("studio.css"));
        var views = new[] { "_PairTable.cshtml", "_Statement.cshtml", "Pair.cshtml", "Index.cshtml", "PairNotFound.cshtml" };

        var emitted = Regex.Matches(string.Concat(views.Select(Source)), @"class=""([^""]*)""")
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"(?<![a-z0-9-@(])\b(a-[a-z0-9-]+)"))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(emitted);

        var undefined = emitted.Where(c => !Regex.IsMatch(css, @"\." + Regex.Escape(c) + @"(?![a-z0-9-])")).ToList();

        Assert.True(undefined.Count == 0, "the views emit classes studio.css does not define: " + string.Join(", ", undefined));
    }

    // ── The vendored tokens are the design system's tokens ──────────────────────────────────────

    /// <summary>
    /// Studio serves its own copy of <c>tokens/*.css</c> rather than linking the design system's, so
    /// there are two files where the system means one and the copy is free to fall behind in
    /// silence. It did: <c>--slot-ident</c> (16px), <c>--slot-ident-page</c> (28px) and
    /// <c>--gap-ident</c> (6px) — the three tokens the Iconography exception added, with the twelve
    /// lines of measurement that argue them — were not in the copy, and nothing said so. A custom
    /// property that is not defined is not an error: <c>height: var(--slot-ident)</c> is dropped at
    /// parse time, with no console message, and the slot falls back to auto.
    ///
    /// So the rule is byte-identity, and the one file that deviates has to argue the deviation in
    /// its own header — which is exactly what <c>fonts.css</c> does over thirty lines, deleting the
    /// system's Google CDN import because this is the first surface of the project anyone opens
    /// without an account. A deviation with an argument is a decision; one without is drift, and
    /// this test is the difference between them.
    /// </summary>
    [Theory]
    [InlineData("base.css")]
    [InlineData("colors.css")]
    [InlineData("effects.css")]
    [InlineData("spacing.css")]
    [InlineData("typography.css")]
    public void The_vendored_token_files_are_the_design_systems_own(string file)
    {
        Assert.Equal(Token("ds-system", file), Token("ds-vendored", file));
    }

    [Fact]
    public void The_identity_slot_the_exception_added_is_on_this_surface_too()
    {
        // Named one by one rather than left to the file comparison above, because these three are
        // what the Iconography exception is FOR, and a future refresh of the system's file that
        // dropped them would keep both copies equal and this surface still without a slot.
        var spacing = Token("ds-vendored", "spacing.css");

        Assert.Contains("--slot-ident:16px", spacing, StringComparison.Ordinal);
        Assert.Contains("--slot-ident-page:28px", spacing, StringComparison.Ordinal);
        Assert.Contains("--gap-ident:6px", spacing, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_token_file_that_deviates_says_why_in_its_own_header()
    {
        var vendored = Token("ds-vendored", "fonts.css");

        Assert.NotEqual(Token("ds-system", "fonts.css"), vendored);

        // The deviation itself: the system's CDN import is gone, and the argument for its going is
        // in the file rather than in a commit message nobody reads beside the stylesheet. Read past
        // the comments, because the argument QUOTES the import it deleted — which is the point of
        // it, and would make a plain substring search report the deletion as still present.
        Assert.DoesNotContain("fonts.googleapis.com", StripComments(vendored), StringComparison.Ordinal);
        Assert.Contains("third party", vendored, StringComparison.Ordinal);
    }

    private static string Token(string tree, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", tree, file));

    private static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

    /// <summary>
    /// The one exception to rule 5, pinned at both ends: candle bodies ARE filled, and nothing
    /// else on the surface borrows that.
    ///
    /// It is pinned because it is an exception, and an exception with no boundary becomes a rule
    /// by drift. The readme states it, RULE-CHANGES entry 10 argues it, and this is what stops
    /// the next person from reading "direction may be a fill" and applying it to a bar, a chip or
    /// a sparkline — none of which are shapes with their own vocabulary, and all of which sit in
    /// a column beside an age.
    /// </summary>
    [Fact]
    public void Candle_bodies_are_filled_and_that_exception_stays_inside_the_candles()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "studio-candles.js"));

        Assert.Contains("upColor: v('--candle-up')", js, StringComparison.Ordinal);
        Assert.Contains("downColor: v('--candle-down')", js, StringComparison.Ordinal);
        Assert.DoesNotContain("upColor: 'rgba(0,0,0,0)'", js, StringComparison.Ordinal);

        // The candle inks belong to the candles. If either turns up in the stylesheet, something
        // outside a chart has started saying direction with a fill.
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "studio.css"));
        Assert.DoesNotContain("--candle-up", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--candle-down", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OHLC LINE RESERVES EVERY FIELD FROM A COUNT, and the count comes from the panel's own
    /// bars.
    ///
    /// This is phase zero's rule arriving on a new surface. An OHLC readout is four figures and a
    /// percentage side by side, all six of them rewritten as the pointer sweeps — the single most
    /// likely place on this page to reintroduce the defect the venue cell had, where a value that
    /// gains a character moves everything to its right. The four figure slots and the change slot
    /// are therefore widths in CHARACTERS, handed down per panel by <c>OhlcLine.Build</c>.
    ///
    /// A literal would pass a rendering and fail on data: the change slot WAS <c>9ch</c>, which is
    /// a guess at a maximum ("sign, three digits, point, two decimals, per-cent") and is one short
    /// of <c>+1,204.55%</c>. So this asserts the shape rather than a number — a width that mentions
    /// no custom property is a width somebody decided on their own.
    /// </summary>
    [Fact]
    public void Every_field_in_the_ohlc_line_is_a_slot_counted_from_the_data()
    {
        var css = Source("studio.css");
        var view = Source("Pair.cshtml");

        // The four figures and the change: each width is computed from a count the server hands
        // down, not from a constant somebody was confident about.
        Assert.Matches(@"width:\s*calc\([^;]*var\(--ohlc-n", Block(css, ".a-ohlc-v"));
        Assert.Matches(@"width:\s*calc\([^;]*var\(--ohlc-chg-n", Block(css, ".a-ohlc-chg"));

        // Both counts are actually emitted. A slot whose custom property nothing sets falls back to
        // one character and clips every figure on the page.
        Assert.Contains("--ohlc-n:@ohlc.FigureGlyphs", view, StringComparison.Ordinal);
        Assert.Contains("--ohlc-chg-n:@ohlc.ChangeGlyphs", view, StringComparison.Ordinal);

        // The hour is the one field whose string is a constant length, so its slot is exact — and it
        // is written in --age-ch, because letter-spacing is added after every character and `ch`
        // does not include it. `9ch` here would clip by nine tracking steps.
        Assert.Matches(@"width:\s*calc\(9 \* var\(--age-ch\)\)", Block(css, ".a-ohlc-when"));

        // The label beside the change is three words in a line of one-character keys, and a flex
        // item wraps its own text. Without this the panel changes height when the columns control
        // narrows it, which is the defect one door along.
        Assert.Matches(@"white-space:\s*nowrap", Block(css, ".a-ohlc-k"));
    }

    /// <summary>
    /// The glossary describes the candle this page actually draws.
    ///
    /// Found in the tree, two commits after the fact: the CANDLES entry still read "Hollow above the
    /// open, filled below" while <c>studio-candles.js</c> had been filling both bodies since
    /// RULE-CHANGES entry 10. The code changed and the prose that explains the code did not, which
    /// is worse here than an unexplained picture — the glossary is where a reader goes when the
    /// picture is ambiguous, and it was answering about a different picture.
    ///
    /// Two directions, because the exception is allowed to be reversed and the entry has to follow
    /// it either way.
    /// </summary>
    [Fact]
    public void The_glossary_describes_the_candle_the_page_draws()
    {
        var js = Source("studio-candles.js");
        var view = Source("Pair.cshtml");

        // Comments stripped — or the entry's own note about what it used to say would satisfy the
        // test it exists to explain — and wrapped to one line, because the sentence is broken across
        // three in the view and a reader does not see the line breaks.
        var prose = Regex.Replace(
            Regex.Replace(view, @"@\*.*?\*@", " ", RegexOptions.Singleline), @"\s+", " ");
        var filled = js.Contains("upColor: v('--candle-up')", StringComparison.Ordinal);

        Assert.Equal(filled, prose.Contains("Both bodies are filled", StringComparison.Ordinal));

        // And the sentence that was left behind cannot come back while the bodies are filled. Stated
        // one-directionally on purpose: the entry is not obliged to use those five words if the
        // exception is ever reversed, only forbidden to use them while it is in force.
        Assert.False(filled && prose.Contains("Hollow above the open", StringComparison.Ordinal),
            "The candles glossary entry describes a hollow candle while studio-candles.js fills both "
            + "bodies. The picture changed (RULE-CHANGES entry 10) and the prose explaining it did not.");
    }

    /// <summary>
    /// Every versioned script tag carries a version the file's own content has earned.
    ///
    /// This exists because the candle fill shipped and did not arrive: the container held the new
    /// file and Cloudflare served the old one for another quarter of an hour, because ?v= is the
    /// cache key and the edit had not touched it. A stale asset behind a fresh deploy is the worst
    /// shape of that failure — the code is right, the page is wrong, and nothing anywhere says so.
    ///
    /// What this actually does is smaller than it sounds, and saying so is the point: it pins the
    /// expected version as a literal here. It does not watch the file's content, so it cannot tell
    /// you that a script changed and its version did not. What it does give is a second place that
    /// must be edited — a bump is now two lines in two files, and the second one is a test whose
    /// failure names the file. That converts a silent omission into a red build, which is the part
    /// that was missing; it does not convert it into an impossibility.
    ///
    /// The other way to lose: fetch the NEW version's URL before the deploy has converged. The edge
    /// caches what the origin still has — the old file, now pinned under the new key — and the bump
    /// is spent without ever having served the new file. studio-ages.js?v=6 shipped exactly that
    /// way and the page it belonged to ran the previous script for half an hour. So a deploy is
    /// probed with a throwaway query (?probe=…), never with the real ?v=, and a version that was
    /// touched before its deploy landed is burnt: bump past it rather than wait it out.
    /// </summary>
    [Theory]
    [InlineData("studio-ages.js", 7)]
    [InlineData("studio-candles.js", 9)]
    [InlineData("studio-live.js", 4)]
    public void A_script_tag_carries_the_version_its_file_has_earned(string file, int expected)
    {
        // The three scripts are split across the two views that load them — the ages tick on every
        // page and live in the layout, the candles and the stream belong to the pair page only.
        var views = new[] { "Pair.cshtml", "_Layout.cshtml" }
            .Select(v => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", v)));

        var tag = views
            .Select(v => System.Text.RegularExpressions.Regex.Match(
                v, System.Text.RegularExpressions.Regex.Escape(file) + @"\?v=(\d+)"))
            .FirstOrDefault(m => m.Success) ?? System.Text.RegularExpressions.Match.Empty;

        Assert.True(tag.Success, $"{file} is not referenced with a ?v= in Pair.cshtml or _Layout.cshtml");
        Assert.Equal(expected, int.Parse(tag.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The TradingView attribution is present, one way or the other. This is a LICENCE test, not a
    /// design test, and it is the only one in this file that guards an obligation to someone else.
    ///
    /// Lightweight Charts is Apache 2.0. Shipping LICENSE and NOTICE beside the vendored file meets
    /// clause 4(d) by itself. TradingView's own documentation for `attributionLogo` states what
    /// they read the licence to require: the notice in the code AND a link to tradingview.com on
    /// the page available to users — with the logo offered as one sufficient way of providing it.
    ///
    /// So there are exactly two compliant states, and this asserts their disjunction rather than
    /// either one: the chart draws the logo, or the page carries the link. It fails only in the
    /// state that is a breach — neither. That shape matters, because the obvious version of this
    /// test would pin the footer text and then quietly forbid the simpler fix.
    /// </summary>
    [Fact]
    public void The_TradingView_attribution_is_on_the_page_one_way_or_the_other()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "studio-candles.js"));
        var layout = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "_Layout.cshtml"));

        var logoDrawn = !js.Contains("attributionLogo: false", StringComparison.Ordinal);

        // The link, not merely the word: an unlinked mention is not what the requirement asks for.
        // Comments are stripped first, or the paragraph explaining the obligation would satisfy it.
        var markup = System.Text.RegularExpressions.Regex.Replace(layout, @"@\*.*?\*@", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var linkOnPage = markup.Contains("href=\"https://www.tradingview.com/\"", StringComparison.Ordinal);

        Assert.True(logoDrawn || linkOnPage,
            "Neither the chart's attribution logo nor a tradingview.com link on the page: "
            + "that is a licence breach. Turn attributionLogo back on in studio-candles.js, "
            + "or restore the attribution line in the footer of _Layout.cshtml.");
    }
}

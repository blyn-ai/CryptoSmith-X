using System.Text.RegularExpressions;

namespace CryptoSmithX.Arena.Tests;

/// <summary>
/// The design system's rules, asserted against the two files that either keep them or do not:
/// <c>arena.css</c> and the table that emits into it.
///
/// <b>Why the text and not a behaviour.</b> Nothing here is reachable from a method call. "Every
/// history slot is the same height", "a number is never wrapped in a Tag" and "no rule is defined
/// that nothing emits" are properties of a stylesheet, and every one of them was broken in a tree
/// that built clean under TreatWarningsAsErrors with all four test projects green. A rule that
/// nothing can fail is a preference; these are rules, so something has to be able to fail.
///
/// Every assertion below was measured in a browser first, at 1920, against a rendered four-venue
/// BTC row. The numbers are in the comments and in <c>arena.css</c>'s own; the tests guard the
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

        Assert.True(rule is not null, $"arena.css has no rule whose selector is exactly `{selector}`");
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
        var css = Source("arena.css");

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
        var css = Source("arena.css");

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
        Assert.Equal(CryptoSmithX.Arena.Format.SparkHeight - CryptoSmithX.Arena.Format.MirrorHeight,
            CryptoSmithX.Arena.Format.SplitSparkHeight);

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
        Assert.Matches(@"line-height:\s*1;", Block(Source("arena.css"), ".a-pair-sep"));
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
        var block = Block(Source("arena.css"), ".a-pair");

        Assert.Matches(@"max-width:\s*100%", block);
        Assert.Matches(@"flex-wrap:\s*wrap", block);

        // Right-aligned on both lines, or the second line breaks the tabular stack the column is
        // read down.
        Assert.Matches(@"justify-content:\s*flex-end", block);
    }

    /// <summary>
    /// Every figure is contained, and not only the two in a depth cell.
    ///
    /// The containment used to live on <c>.a-pair</c> alone, and the mechanism it fixes has nothing
    /// to do with depth: <c>.a-cell</c> is a flex column at <c>align-items: flex-end</c>, so every
    /// <c>.a-fig</c> is a content-sized item with no maximum and hangs LEFT out of its own column,
    /// over the right-aligned figure of the column before it. Migration 0023's contract multipliers
    /// of 1000 and 1000000 make that the ordinary printed value on those pairs, not an edge case:
    /// open interest at 122.4px in an 84px content box, 28.4px of it inside the turnover cell.
    /// </summary>
    [Fact]
    public void Every_figure_is_contained_by_its_own_column_and_not_only_a_depth_pair()
    {
        var fig = Block(Source("arena.css"), ".a-fig");

        Assert.Matches(@"max-width:\s*100%", fig);
        Assert.Matches(@"overflow-wrap:\s*anywhere", fig);

        // And `nowrap` is gone, because it is what made the two declarations above unreachable: a
        // line that may not break cannot be contained by a box it does not fit.
        Assert.DoesNotMatch(@"white-space:\s*nowrap", fig);
    }

    [Fact]
    public void A_depth_pair_still_breaks_between_its_two_figures_before_either_one_breaks()
    {
        // The order matters and it is the whole reason the last-resort break is admissible. A flex
        // line is broken BETWEEN items before any item's own text is, so the ask moves whole
        // whenever it fits alone — which is every case the pair wrap was written for. Delete
        // `flex-wrap` and the two figures stop having a break of their own to take first.
        var pair = Block(Source("arena.css"), ".a-pair");

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
        Assert.Matches(@"justify-content:\s*flex-start", Block(Source("arena.css"), ".a-cell"));
    }

    [Fact]
    public void The_figure_is_never_clipped_and_never_shortened_to_fit()
    {
        var css = Source("arena.css");

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
        var css = Source("arena.css");
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
    public void Every_class_arena_css_defines_is_emitted_by_something()
    {
        var css = Source("arena.css");
        var emitters = new[]
        {
            "_PairTable.cshtml", "_Statement.cshtml", "_Layout.cshtml", "_Stamps.cshtml",
            "Pair.cshtml", "Index.cshtml", "PairNotFound.cshtml",
            "arena-ages.js", "arena-live.js", "arena-candles.js"
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

        Assert.True(orphans.Count == 0, "arena.css defines rules nothing emits: " + string.Join(", ", orphans));
    }

    [Fact]
    public void Every_class_the_views_emit_is_defined()
    {
        // The other direction, and the one that renders as an unstyled cell rather than as nothing:
        // a view that emits `a-mark-note` against a stylesheet that still says `a-tag--note` puts
        // tracked, non-tabular, full-strength digits in the mark slot of four cells.
        var css = StripComments(Source("arena.css"));
        var views = new[] { "_PairTable.cshtml", "_Statement.cshtml", "Pair.cshtml", "Index.cshtml", "PairNotFound.cshtml" };

        var emitted = Regex.Matches(string.Concat(views.Select(Source)), @"class=""([^""]*)""")
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"(?<![a-z0-9-@(])\b(a-[a-z0-9-]+)"))
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(emitted);

        var undefined = emitted.Where(c => !Regex.IsMatch(css, @"\." + Regex.Escape(c) + @"(?![a-z0-9-])")).ToList();

        Assert.True(undefined.Count == 0, "the views emit classes arena.css does not define: " + string.Join(", ", undefined));
    }

    // ── The vendored tokens are the design system's tokens ──────────────────────────────────────

    /// <summary>
    /// Arena serves its own copy of <c>tokens/*.css</c> rather than linking the design system's, so
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
}

using System.Text.RegularExpressions;

namespace CryptoSmithX.Arena.Tests;

/// <summary>
/// The two controls on the pair page that are not the register flip, and the one property they
/// share: a control on this surface may not promise anything the page cannot do.
///
/// Both were added for the same complaint. The reader said the layout did not match the reference
/// and that the live toggle was missing — and the toggle was not missing, it was a 54-by-32 button
/// with one word on it and nothing to say what pressing it would change. A control that works and
/// is never found has failed, and these assertions are about the half of a control that is not its
/// mechanism.
/// </summary>
public sealed class ControlsTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    /// <summary>Line breaks flattened, so a rewrap of a 100-column source cannot fail a sentence.</summary>
    private static string Prose(string name) => Regex.Replace(Read(name), @"\s+", " ");

    // ── The columns control ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_columns_control_offers_every_arrangement_and_the_sheet_answers_all_of_them()
    {
        var view = Read("Pair.cshtml");
        var css = Read("arena.css");

        // Three buttons, and each one names the layout it asks for rather than a number: this is
        // the only control on the page whose icon IS its label, so the title carries the words.
        //
        // "UP TO", ON THE TWO THAT CANNOT PROMISE THE COUNT. The declarations below resolve a
        // CEILING — at 1200px `data-cols="3"` draws two tracks — and the button stayed titled
        // "3 columns" and pressed while the page showed two, on every later pair, because the
        // choice is stored. One column has no ceiling to miss: its track is `minmax(0, 1fr)`,
        // which is exactly one at every width, so it is named exactly.
        foreach (var title in new[] { "Up to 3 columns", "Up to 2 columns", "1 column" })
        {
            Assert.Contains($"title=\"{title}\"", view, StringComparison.Ordinal);
        }

        foreach (var count in new[] { "3 columns", "2 columns" })
        {
            Assert.DoesNotContain($"title=\"{count}\"", view, StringComparison.Ordinal);
        }

        // And a declaration behind every value it can set. A button that sets an attribute nothing
        // styles is the same lie as a button with no handler — it presses, it reports pressed, and
        // the page does not move.
        foreach (var n in new[] { "1", "2", "3" })
        {
            Assert.Contains($"data-cols=\"{n}\"", view, StringComparison.Ordinal);
            Assert.Contains($".a-charts[data-cols=\"{n}\"]", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_choice_is_a_ceiling_and_never_narrower_than_a_readable_panel()
    {
        var css = Prose("arena.css");

        // The ceiling: three columns on a laptop must come out as two rather than as three pictures
        // of charts, so the minimum track is the larger of the readable width and one N-th of the
        // row. `repeat(3, 1fr)` is the spelling that loses this, and it is the one to watch for.
        Assert.DoesNotContain("grid-template-columns: repeat(3,", css, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns: repeat(2,", css, StringComparison.Ordinal);
        Assert.Contains("max(var(--a-chart-min)", css, StringComparison.Ordinal);

        // And the floor is capped at the container, or the floor becomes an overflow: measured at
        // 390px, an uncapped 460px minimum left the panel hanging 122px outside the grid.
        Assert.Contains("minmax(min(100%, max(var(--a-chart-min)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_section_opens_in_one_column_the_way_the_reference_does()
    {
        var css = Prose("arena.css");

        // Measured on the live reference at 1920, 1440 and 1280: one grid track the full width of
        // the sheet, seven panels down the page, and the "1 column" button already pressed. The
        // auto-fit default that was here landed on three tracks at 1920 and two at 1440, so the two
        // pages opened at different layouts with no interaction at all — and the control's own
        // initialisation, which counts the tracks the sheet drew, then reported the difference as
        // the reader's choice.
        //
        // It is also the arrangement this section argues for: the panels share one price scale per
        // quote asset, and a shared scale is only readable stacked.
        Assert.Contains(".a-charts { --a-chart-min: 460px; display: grid;"
            + " grid-template-columns: minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.DoesNotContain("minmax(min(100%, var(--a-chart-min)), 1fr)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_grid_has_a_layout_before_any_script_runs_and_the_control_does_not()
    {
        var view = Read("Pair.cshtml");
        var css = Prose("arena.css");
        var js = Read("arena-candles.js");

        // Scripts off: the grid still packs as many panels as fit, which is the layout this page
        // has always drawn. The rule with no [data-cols] is what makes that true, so it has to keep
        // existing separately from the three that answer the control.
        Assert.Contains(".a-charts { --a-chart-min: 460px; display: grid;", css, StringComparison.Ordinal);

        // Scripts off: the control is not there at all. Same argument as the Ink and Live buttons —
        // it could do nothing, and a control that does nothing is a small lie.
        Assert.Matches(new Regex(@"class=""a-cols""[^>]*\shidden", RegexOptions.Singleline), view);
        Assert.Contains("cols.hidden = false;", js, StringComparison.Ordinal);

        // An author rule setting `display` beats the UA sheet's [hidden], so the attribute above is
        // inert unless the stylesheet says so itself.
        Assert.Contains(".a-cols[hidden] { display: none; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_is_told_why_one_column_exists()
    {
        // The control is not a density preference. The panels share one price scale per quote asset
        // and a shared scale is only legible stacked — one column puts the same price at the same
        // height on the page. If the page offers the arrangement without stating the reading it
        // makes possible, it has shipped the switch and kept the point to itself.
        var reading = Prose("Pair.cshtml");
        Assert.Contains("Stack them in one column", reading, StringComparison.Ordinal);
        Assert.Contains("the shared scale is what makes that reading valid", reading, StringComparison.Ordinal);
    }

    [Fact]
    public void The_arrangement_is_remembered_per_viewer_and_only_once_it_is_asked_for()
    {
        var js = Read("arena-candles.js");

        // The same contract as the register flip: written on a press, never on an arrival, and the
        // read is guarded because private mode throws on the property access rather than returning
        // null. Nothing is stored for a reader who never asks for anything.
        Assert.Contains("const KEY = 'csx-arena-cols';", js, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem(KEY,", js, StringComparison.Ordinal);
        Assert.Contains("localStorage.getItem(KEY)", js, StringComparison.Ordinal);

        Assert.Equal(2, Regex.Matches(js, @"catch \(e\) \{ /\* Private mode").Count);
    }

    // ── The live control, and the document's own age ────────────────────────────────────────────

    [Fact]
    public void The_live_button_has_a_sentence_and_the_sentence_names_the_button()
    {
        var view = Read("Pair.cshtml");
        var js = Read("arena-ages.js");

        // The discoverability fix, and the whole of it: the button's label is the name of a state
        // the reader cannot see, so the sentence beside it says which state the page is in and what
        // pressing the button changes. Tied to the button for a screen reader as well as for an eye.
        Assert.Contains("aria-describedby=\"a-live-what\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"a-live-what\"", view, StringComparison.Ordinal);
        Assert.Contains("This page is a snapshot.", js, StringComparison.Ordinal);
        Assert.Contains("until you press Live.", js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sentence_is_written_by_the_clock_and_never_served_as_html()
    {
        // With scripts off the ages do NOT count forward, so a server-rendered "the ages below
        // count forward from the render" would be false on exactly the pages that cannot correct
        // it. The element ships empty and the file that moves the clock is the file that fills it.
        var view = Prose("Pair.cshtml");
        Assert.Contains(
            "<p class=\"a-live-what\" id=\"a-live-what\" role=\"status\"></p>", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_admitting_it_has_gone_stale_is_announced_and_not_only_described()
    {
        var view = Prose("Pair.cshtml");

        // aria-describedby alone announces this sentence only to a reader who has put focus on the
        // Live button. It also REWRITES ITSELF under a reader who has done nothing: a tab comes
        // back from an hour in the background, a box appears saying the document is an hour old and
        // a Reload button materialises beside it, and a screen reader was told none of it — while
        // the connection note in the same row, the less consequential of the two, carried
        // role="status" and was announced. The fact that governs whether every figure on the page
        // still means anything is not the one to leave silent.
        Assert.Contains("id=\"a-live-what\" role=\"status\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"a-live-note\" role=\"status\"", view, StringComparison.Ordinal);

        // And it stays the Live button's description as well: the two are different readings of the
        // same sentence, not alternatives.
        Assert.Contains("aria-describedby=\"a-live-what\"", view, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence the page prints about itself, with its own reasoning stripped out.
    ///
    /// The assertions below are about what a READER can be shown, and a comment is not that. The
    /// argument for the rewrite quotes the sentence it replaced, so a plain search of this block
    /// would find the old claim in the paragraph explaining why it is gone.
    /// </summary>
    private static string StaleSentence()
    {
        var js = Read("arena-ages.js");
        var block = js[js.IndexOf("const sayPageState", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("let cells = []", StringComparison.Ordinal)];

        return string.Join('\n', block
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_stale_page_is_gated_on_its_own_age_and_not_on_a_feed_crossing()
    {
        var js = Read("arena-ages.js");
        var said = StaleSentence();

        // THE GATE IS THE THING THE SENTENCE IS ABOUT. It was `freshlyDegraded > 0` — a feed
        // crossing into degraded after the render — which is an event in the FEEDS used to decide
        // whether to say something about the DOCUMENT, and the two come apart on exactly the page
        // this row exists for: every feed already degraded when the server built it, nothing left
        // to cross, and a tab open for an hour over a screen of `degraded` that never states its
        // age, never shows the box and never offers Reload.
        Assert.DoesNotContain("const stale = freshlyDegraded > 0;", js, StringComparison.Ordinal);
        Assert.Contains("const aged = fastestWinS !== null && degraded(docAgeS, fastestWinS);",
            said, StringComparison.Ordinal);
        Assert.Contains("const stale = aged || freshlyDegraded > 0;", said, StringComparison.Ordinal);

        // And no new constant. The threshold is the same `degraded` predicate three other
        // judgements in this file are made with, asked of the document against the quickest window
        // the SERVER sent — never a flat number of seconds, which is the bug this surface has
        // already shipped once and which the file's own header rejects by name.
        Assert.Contains("fastestWinS = call.win;", js, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"docAgeS\s*[<>]=?\s*\d"), said);
    }

    [Fact]
    public void The_stale_sentence_states_the_document_age_and_never_a_cause()
    {
        var said = StaleSentence();

        // THE DEFECT, AND THE ONE THIS TEST EXISTS FOR. The row used to describe every feed that
        // was not already degraded at the render as a mark that "dates the page, not the venue".
        // The page cannot know that: with no stream open, a venue that died one second after the
        // render and a venue still publishing produce exactly the same evidence, because nothing
        // has been re-read since. Worse, it was demonstrably false for a call already past its
        // window at the render — 800 s into a 300 s window is not degraded yet, so it crossed under
        // the reader, counted as freshly degraded, and was exonerated by name over a venue that had
        // been silent for more than two windows before the page existed. The reassurance pointed at
        // the one failure the reader most needed to see.
        foreach (var cause in new[]
        {
            "dates the page", "date the page", "not the venue", "not the venues",
            "the venue is fine", "the venues are fine", "only your tab", "just your tab"
        })
        {
            Assert.DoesNotContain(cause, said, StringComparison.OrdinalIgnoreCase);
        }

        // What it says instead: how old the document is, that nothing has been re-read, and that
        // which of the two happened is not a thing this page knows. The unknown is stated, not
        // resolved in our favour.
        Assert.Contains("'Rendered ' + durationText(docAgeS) + ' ago, and nothing below has been re-read since. '",
            said, StringComparison.Ordinal);
        Assert.Contains("this page cannot tell you why", said, StringComparison.Ordinal);
        Assert.Contains("look exactly the same from here", said, StringComparison.Ordinal);

        // The two ways out are still offered, because "the page cannot know" is only half an answer
        // if nothing on the page can go and find out.
        Assert.Contains("Reload for a fresh read", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_whose_feeds_were_all_degraded_at_the_render_still_states_its_own_age()
    {
        var said = StaleSentence();

        // The page the original complaint describes, and the one the old gate kept silent. Nothing
        // here is the document's doing, so nothing is counted against it — and the document is
        // still old, which is still a fact the reader is owed.
        Assert.Contains("} else if (marked > 0) {", said, StringComparison.Ordinal);
        Assert.Contains("were already degraded at the render", said, StringComparison.Ordinal);
        Assert.Contains("What has happened at any venue since is not something this page can know.",
            said, StringComparison.Ordinal);

        // And the third branch, which the gate cannot reach today — being aged past the quickest
        // window on the page by the degraded multiple means the call carrying that window is itself
        // degraded — is written all the same, so that loosening the gate can never leave this
        // sentence undefined. Pinned here for the same reason it is written there.
        Assert.Contains("What the venues have done in that time is not something this page can know",
            said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_ways_out_of_a_stale_page_are_both_offered_and_told_apart()
    {
        var view = Read("Pair.cshtml");
        var js = Read("arena-ages.js");

        // Reload asks the server for a fresh read of every figure; Live leaves this document
        // standing and replaces the table as passes land. A reader looking at a screen of degraded
        // marks wants the first, so the sentence puts it first and the button appears beside it.
        Assert.Contains("id=\"a-live-reload\"", view, StringComparison.Ordinal);
        Assert.Contains("Reload for a fresh read", js, StringComparison.Ordinal);
        Assert.Contains("window.location.reload()", js, StringComparison.Ordinal);

        // Hidden until the document has actually aged. A reload control standing beside a page that
        // is thirty seconds old is furniture.
        Assert.Matches(new Regex(@"id=""a-live-reload""[^>]*\shidden", RegexOptions.Singleline), view);
        Assert.Contains("reloadButton.hidden = !stale;", js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feed_the_server_found_degraded_is_never_blamed_on_the_open_tab()
    {
        var js = Read("arena-ages.js");

        // The half of the reader's question that has a real answer. A feed the SERVER found
        // degraded is an observation about a venue and stays one however long the tab is left open,
        // so it is counted apart from the ones the tab's own age produced — and a page where every
        // degraded feed was already degraded at the render says nothing about itself at all,
        // because none of those marks is the document's doing.
        Assert.Contains("alreadyDegraded += 1;", js, StringComparison.Ordinal);
        Assert.Contains("already degraded at the render, and that is a fact about the venue", js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_age_claim_never_makes_a_figure_look_fresher_than_it_is()
    {
        var js = Read("arena-ages.js");

        // The one property this whole surface rests on. The row states a fact about the DOCUMENT;
        // it does not reach into a cell, so there is no path here that removes a △, un-fades a
        // figure or shortens an age. The withdrawal above this is the only code in this file that
        // touches a claim, and it only ever takes one away.
        var stale = js[js.IndexOf("const sayPageState", StringComparison.Ordinal)..];
        stale = stale[..stale.IndexOf("let cells = []", StringComparison.Ordinal)];

        foreach (var forbidden in new[] { "a-age--spent", "a-tri", "classList.remove", "--w" })
        {
            Assert.DoesNotContain(forbidden, stale, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_live_stream_makes_the_document_new_again()
    {
        var js = Read("arena-ages.js");

        // The fragments a push carries were rendered at the instant it sends, so the document is
        // new and its age starts over. Without this the stale row would stand over a table that had
        // just been replaced — the same class of error as the statement line standing still while
        // the ages under it moved, which is what this file was written to fix.
        Assert.Contains("renderedAt = at;", js, StringComparison.Ordinal);

        // And the render instant is not the clock correction. A HEAD on the way back to a tab moves
        // `serverNow` and must not be allowed to declare the page freshly rendered.
        Assert.Contains("let renderedAt = serverNow;", js, StringComparison.Ordinal);
        var resync = js[js.IndexOf("const resync = async", StringComparison.Ordinal)..];
        resync = resync[..resync.IndexOf("document.addEventListener('visibilitychange'", StringComparison.Ordinal)];
        Assert.DoesNotContain("renderedAt", resync, StringComparison.Ordinal);
    }
}

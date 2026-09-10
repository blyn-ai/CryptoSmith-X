using System.Text.RegularExpressions;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The headline stopped changing length.
///
/// It used to be one of five sentences, so the largest type on the page moved whenever the page
/// changed state. The words are fixed now and only two figures move, each in a slot of its own —
/// and the figures are set in the mono face, because the display face ships no tabular figures and
/// a slot would hold the LINE still while the digits inside it kept shifting. That objection is
/// recorded on Statement.OneCallLate; this is how it is paid rather than argued with.
/// </summary>
public sealed class StatementCountsTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void A_page_with_nothing_to_judge_shows_a_dash_and_never_a_zero()
    {
        // Two different silences, and neither of them is "everything is inside its window": a zero
        // there would be the page claiming a verdict it has no observations for.
        var nothing = Statement.Counts([]);

        Assert.Null(nothing.Late);
        Assert.Null(nothing.Degraded);
        Assert.Equal("—", StatementCounts.Slot(nothing.Late));
    }

    [Fact]
    public void A_count_prints_as_itself()
    {
        Assert.Equal("0", StatementCounts.Slot(0));
        Assert.Equal("7", StatementCounts.Slot(7));
    }

    [Fact]
    public void The_words_are_constant_and_only_the_slots_carry_a_figure()
    {
        var view = Read("AssetV2.cshtml");

        // The counts live in their own elements; nothing else in the heading is generated, so the
        // sentence around them cannot change width.
        Assert.Matches(@"data-statement-late[^>]*>@StatementCounts\.Slot\(counts\.Late\)</b>", view);
        Assert.Matches(@"data-statement-degraded[^>]*>@StatementCounts\.Slot\(counts\.Degraded\)</b>", view);
        Assert.DoesNotContain("Statement.Verdict(Model.Rows)", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_figures_leave_the_display_face_because_it_has_no_tabular_digits()
    {
        Assert.Matches(
            @"\.v2-statement b\{[^}]*font-family:var\(--font-mono\)",
            Read("studio-v2.css"));
        Assert.Matches(
            @"\.v2-statement b\{[^}]*text-align:right",
            Read("studio-v2.css"));
    }

    [Fact]
    public void The_ticker_writes_slots_where_they_exist_and_the_sentence_where_they_do_not()
    {
        // One ticker serves both pages: the first design still keeps its five sentences, and
        // neither page rewrites the other's markup.
        var js = Read("studio-ages.js");

        Assert.Contains("data-statement-late", js, StringComparison.Ordinal);
        Assert.Contains("Every call is inside its window.", js, StringComparison.Ordinal);
        Assert.Matches(@"judged \? String\(lateCalls\) : '—'", js);
    }
}

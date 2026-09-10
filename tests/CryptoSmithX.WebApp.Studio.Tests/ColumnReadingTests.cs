using System.Text.RegularExpressions;
using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The three things the collapsed table has to do that it was not doing, asserted at the level each
/// one actually lives at.
///
/// The page exists to answer "how does this figure stand across seven venues". That question is
/// read DOWN a column, and until now the only hover state ran across a row, ranks were computed and
/// never shown, and the direction of each figure was only visible one field at a time in band 3.
/// </summary>
public sealed class ColumnReadingTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void Every_ranked_column_is_reachable_from_a_figure_on_the_page()
    {
        // Verdicts ranks twelve columns. A column ranked and never rendered is arithmetic nobody
        // sees — which is what both book sizes, depth 10 and 50, and both sides of the top of book
        // were until the marks moved into the collapsed row.
        var row = Rows.At(Rows.Venue(1));

        var fromFields = V2Groups.All
            .SelectMany(g => g.Fields)
            .Select(f => V2Ranks.ColumnOf(f.Field));

        var fromCells = V2Groups.All
            .Select(g => V2Cells.Head(row, g, VerdictTable.Empty))
            .SelectMany(h => h.Big.Concat(h.Small))
            .Select(p => p.Column);

        var shown = fromFields.Concat(fromCells).Where(c => c is not null).Select(c => c!.Value).ToHashSet();

        // Prompt 2, U-12: BID SIZE / ASK SIZE dropped their MARK on purpose — top-of-book size is
        // transient, so a MAX/MIN chip on it stated a fact about an instant rather than the book.
        // The figure itself is still rendered (V2Groups.All's "Bid / Ask size" field); it is only
        // V2Ranks.ColumnOf, the mark's own pathway, that no longer names these two.
        var unmarkedOnPurpose = new[] { PairColumn.BidSize, PairColumn.AskSize };

        foreach (var column in Enum.GetValues<PairColumn>().Except(unmarkedOnPurpose))
        {
            Assert.Contains(column, shown);
        }
    }

    [Fact]
    public void Every_other_ranked_field_agrees_best_with_max()
    {
        // Bid: the good price is the HIGH one (Verdicts.HighIsBest), so Best is the row holding
        // the population's own maximum and wears the "best" class and the MAX mark together.
        Assert.Equal("best", V2Ranks.ClassWord(V2Field.Bid, Verdict.Best));
        Assert.Equal("max", V2Ranks.Mark(V2Field.Bid, Verdict.Best));
        Assert.Equal("worst", V2Ranks.ClassWord(V2Field.Bid, Verdict.Worst));
        Assert.Equal("min", V2Ranks.Mark(V2Field.Bid, Verdict.Worst));
    }

    [Fact]
    public void Ask_and_spread_invert_best_and_worst_against_max_and_min()
    {
        // Prompt 1.5 (UX audit): MAX/MIN name the actual highest/lowest figure, which is a
        // different question from Verdict.Best/Worst on the two ranked fields where the GOOD
        // price is the LOW one (Verdicts.HighIsBest is false for Ask and for Spread) — Best there
        // is the row holding the MINIMUM, so it wears the "worst" class and the MIN mark, and
        // Worst wears "best"/MAX. Same rank, same population, the word just states which extreme
        // rather than which one is good.
        Assert.Equal("worst", V2Ranks.ClassWord(V2Field.Spread, Verdict.Best));
        Assert.Equal("min", V2Ranks.Mark(V2Field.Spread, Verdict.Best));
        Assert.Equal("best", V2Ranks.ClassWord(V2Field.Spread, Verdict.Worst));
        Assert.Equal("max", V2Ranks.Mark(V2Field.Spread, Verdict.Worst));

        Assert.Equal("worst", V2Ranks.ClassWord(V2Field.Ask, Verdict.Best));
        Assert.Equal("min", V2Ranks.Mark(V2Field.Ask, Verdict.Best));
        Assert.Equal("best", V2Ranks.ClassWord(V2Field.Ask, Verdict.Worst));
        Assert.Equal("max", V2Ranks.Mark(V2Field.Ask, Verdict.Worst));
    }

    [Fact]
    public void A_marks_title_states_the_fact_and_the_population()
    {
        Assert.Equal(
            "Highest bid among USD listings",
            V2Ranks.Title(V2Field.Bid, Verdict.Best, quoteScoped: true, quoteAsset: "USD"));
        Assert.Equal(
            "Lowest spread among all listings",
            V2Ranks.Title(V2Field.Spread, Verdict.Best, quoteScoped: false, quoteAsset: "USD"));
    }

    [Fact]
    public void Five_of_the_seven_groups_keep_a_history_and_two_do_not()
    {
        // A row with every series present, so the split below is about the GROUPS and not about
        // this venue's data.
        var row = Rows.At(
            Rows.Venue(1),
            metrics: new MetricHourSeries(
                [.. Enumerable.Range(0, 3).Select(i => DateTime.UnixEpoch.AddHours(i))],
                [.. Enumerable.Range(0, 3).Select(i => (MetricHourRow?)new MetricHourRow(
                    1, DateTime.UnixEpoch.AddHours(i), 1, 0.0001, 100, 50, 60, 60, 0))]),
            liquidations: [1, 2, 3]);

        var withHistory = V2Groups.All.Where(g => V2Groups.Spark(row, g).Count > 0).Select(g => g.Key).ToList();
        var without = V2Groups.All.Where(g => V2Groups.Spark(row, g).Count == 0).Select(g => g.Key).ToList();

        Assert.Equal(["price", "liquidity", "positions", "carry", "stress"], withHistory);

        // Turnover is one rolling figure and an age is measured against now: neither has an hourly
        // series to draw, and rule 11 leaves them the log bar alone.
        Assert.Equal(["activity", "trust"], without);
    }

    [Fact]
    public void The_column_under_the_cursor_is_lit_from_the_document_not_from_the_cells()
    {
        // Delegated, because the live stream replaces band 1 whole: handlers bound to cells would
        // leave with the first push, silently, exactly when the page is most worth watching.
        var js = Read("studio-v2.js");

        Assert.Contains("document.addEventListener('pointerover'", js, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('focusin'", js, StringComparison.Ordinal);
        Assert.DoesNotContain(".v2-cell').forEach(function (el) { el.addEventListener('pointerover'", js, StringComparison.Ordinal);

        // The header of the column is lit with its cells — the group name IS the column's label,
        // and a lit column under an unlit heading reads as a table that lost its top row.
        var css = Read("studio-v2.css");
        Assert.Matches(@"\.v2-cell\.is-col,\.v2-hcell\.is-col", css);
    }
}

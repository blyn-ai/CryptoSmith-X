using CryptoSmithX.Arena.Data;
using CryptoSmithX.Arena.Models;

namespace CryptoSmithX.Arena.Tests;

/// <summary>
/// The one number the stylesheet needs in order to print a figure that does not fit its column.
///
/// <b>What this is for.</b> Seventeen columns at 1836px with no horizontal scroll at 1920 is rule
/// 12, so a figure wider than its track cannot be given a wider track; and a figure is a number, so
/// it cannot be broken, clipped or abbreviated either — the deployed page did break them, printing
/// "6,521,172," above "000" on eleven cells of PEPE/USD. What is left is fitting the type to the
/// column, and the fit needs the length of the longest figure in that column.
///
/// The count lives in C# and the width lives in CSS, on purpose. <c>arena.css</c> holds the
/// seventeen track widths in one custom property and says why a second copy of them is a copy free
/// to drift; nothing here knows a pixel. These tests are about the count and about the three things
/// that make it the COLUMN's count rather than a cell's.
/// </summary>
public sealed class FigureFitTests
{
    private static IReadOnlyList<IReadOnlyList<MetricCellModel>> Cells(
        IReadOnlyList<VenueRowModel> rows)
    {
        var verdicts = Verdicts.Compute(rows);
        var scales = ColumnScales.Compute(rows);
        return [.. rows.Select(r => RowCells.Build(r, verdicts, scales))];
    }

    private static int Column(IReadOnlyList<MetricCellModel> cells, string label)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (string.Equals(cells[i].Label, label, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"no column labelled `{label}`");
    }

    /// <summary>
    /// The count is the column's longest figure, taken across every row — not the row being drawn.
    ///
    /// This is the whole reason the view builds all five rows before it emits the first one. A cell
    /// fitted to itself puts 119,386,224,822,000 at one size and the 8,258,841 above it at another,
    /// in the same right-aligned column, which is the stack the comparison is read down.
    /// </summary>
    [Fact]
    public void A_columns_count_is_its_longest_figure_on_any_row()
    {
        // Base units through the contract multiplier, which is what makes this ordinary rather than
        // exotic: 119,386,224,822 lots of 1000 is BONK's real open interest, and it prints as
        // nineteen characters. Its neighbour on the same page prints nine.
        var rows = Rows.Live(
            Rows.Venue(1, multiplier: 1000, openInterest: 119_386_224_822),
            Rows.Venue(2, multiplier: 1000, openInterest: 8_258));

        var cells = Cells(rows);
        var oi = Column(cells[0], "Open interest");

        Assert.Equal("119,386,224,822,000", cells[0][oi].Text);
        Assert.Equal("8,258,000", cells[1][oi].Text);

        // One number for the column, and it is the longer one — on both rows, because the sheet
        // reads it off whichever cell it happens to be drawing.
        Assert.Equal(19, RowCells.FigureGlyphs(cells)[oi]);
    }

    /// <summary>
    /// A depth cell holds two figures and the count is the wider of them, never their sum.
    ///
    /// The pair has a break of its own — <c>.a-pair</c> wraps BETWEEN the bid and the ask, so each
    /// stays whole — and counting "16,400,000 / 15,900,000" as one 23-character figure would shrink
    /// all three depth columns to fit a string that never has to be on one line.
    /// </summary>
    [Fact]
    public void A_depth_pair_counts_its_wider_figure_and_not_the_two_together()
    {
        var rows = Rows.Live(
            Rows.Venue(1, depthBid25: 1_640_000, depthAsk25: 950),
            Rows.Venue(2, depthBid25: 500, depthAsk25: 400));

        var cells = Cells(rows);
        var depth = Column(cells[0], "Depth 25bps");

        Assert.Equal("1,640,000", cells[0][depth].Text);
        Assert.Equal("950", cells[0][depth].Second);

        // Nine, which is "1,640,000" — not 9 + 3 + the separator.
        Assert.Equal(9, RowCells.FigureGlyphs(cells)[depth]);
    }

    /// <summary>
    /// A column where nothing was measured counts one, never zero.
    ///
    /// The sheet divides the column's width by this number. Zero makes the whole declaration invalid
    /// at computed-value time, which for an inherited property is not a fallback to the line above —
    /// it is the parent's font size, so a column of dashes would print at the body's 14px in the
    /// middle of a 12px table, and no error anywhere says so.
    /// </summary>
    [Fact]
    public void A_column_of_dashes_still_counts_one()
    {
        var rows = Rows.Live(Rows.Venue(1), Rows.Venue(2));

        var cells = Cells(rows);
        var mark = Column(cells[0], "Mark");

        Assert.Equal("—", cells[0][mark].Text);
        Assert.Equal(1, RowCells.FigureGlyphs(cells)[mark]);
        Assert.DoesNotContain(0, RowCells.FigureGlyphs(cells));
    }

    /// <summary>
    /// Every metric column gets a count, so the view can index the list by the column it is drawing.
    /// </summary>
    [Fact]
    public void There_is_one_count_per_metric_column()
    {
        var rows = Rows.Live(Rows.Venue(1, bid: 64_000, ask: 64_001));
        var cells = Cells(rows);

        Assert.Equal(cells[0].Count, RowCells.FigureGlyphs(cells).Count);
    }

    /// <summary>
    /// A page with no venues on it asks for no counts and does not throw doing so.
    /// </summary>
    [Fact]
    public void A_page_with_no_rows_counts_nothing()
    {
        Assert.Empty(RowCells.FigureGlyphs([]));
    }
}

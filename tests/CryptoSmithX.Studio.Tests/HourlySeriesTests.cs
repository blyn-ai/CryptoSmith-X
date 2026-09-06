using CryptoSmithX.Studio;
using CryptoSmithX.Studio.Data;
using CryptoSmithX.Studio.Models;

namespace CryptoSmithX.Studio.Tests;

/// <summary>
/// Rule 11's seven lines, and the four of them that were not being drawn.
///
/// <b>What was wrong.</b> Spread, funding, open interest and depth 25bps are the four columns rule
/// 11 gives an hourly line to that the price series cannot supply. Their series is
/// <c>market_metric_hour</c>, and 0025 withheld that table from <c>studio_reader</c> on one sentence
/// — "агрегаты, которых страница пары не показывает" — which is a claim about this page and is
/// false: those are the four columns. So the page reserved eleven pixels in each of them and drew
/// nothing there, which is not a neutral outcome: it is the same empty slot mark and index show,
/// and rule 11 gives mark and index no second dimension at all. The page said "no history" where
/// it meant "history I cannot read", in pixels the reader cannot tell apart.
///
/// 0026 grants the table. These tests are what stops the four lines quietly going away again — a
/// null series draws no line and throws nothing, so nothing else in this suite would notice.
/// </summary>
public sealed class HourlySeriesTests
{
    private static readonly DateTime[] Windows =
        [.. Enumerable.Range(0, 4).Select(i => DateTime.UnixEpoch.AddHours(i))];

    private static MetricHourSeries Series(params MetricHourRow?[] hours) => new(Windows, hours);

    private static MetricHourRow Hour(
        int i, double? spread = 1, double? funding = 0.0001, double? oi = 100,
        double? depthBid = 50, double? depthAsk = 60, short snapshots = 60) =>
        new(1, Windows[i], spread, funding, oi, depthBid, depthAsk, snapshots);

    /// <summary>
    /// The cells of one venue's row, on a page holding two.
    ///
    /// Two and not one because a bar is a comparison: <c>ColumnScales</c> gives a group of one no
    /// maximum at all, on the same argument the verdicts use — a bar at full width against itself
    /// says "largest" and there is nothing else there. Depth 25bps has to carry its mirrored bar
    /// for these tests to be about the column rule 11 gives BOTH marks to.
    /// </summary>
    private static IReadOnlyList<MetricCellModel> Cells(MetricHourSeries metrics)
    {
        var row = Rows.Venue(1, bid: 100, ask: 101, openInterest: 5, depthBid25: 7, depthAsk25: 8,
            funding: 0.0001);
        var beside = Rows.Venue(2, bid: 100, ask: 102, openInterest: 4, depthBid25: 9, depthAsk25: 6,
            funding: 0.0002);

        var venue = Rows.At(row, metrics: metrics);
        var rows = new[] { venue, Rows.At(beside) };

        return RowCells.Build(venue, Verdicts.Compute(rows), ColumnScales.Compute(rows));
    }

    private static MetricCellModel Cell(IReadOnlyList<MetricCellModel> cells, string label) =>
        cells.Single(c => c.Label == label);

    // ── The four columns draw their line ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Spread bps", CallTone.Ticker)]
    [InlineData("Funding", CallTone.Ticker)]
    [InlineData("Open interest", CallTone.OpenInterest)]
    [InlineData("Depth 25bps", CallTone.Depth)]
    public void The_four_columns_rule_11_names_carry_a_line_in_their_own_calls_colour(
        string label, CallTone tone)
    {
        // The tone is half the point. Rule 4 says colour means WHICH CALL wrote the figure, and
        // before these four existed every line on the page was ticker magenta — CallBands.jsx's
        // claim that "the same three hues reappear on every bar, line and tick below" was true of
        // the bars and the washes and false of the lines.
        var cell = Cell(Cells(Series(Hour(0), Hour(1), Hour(2), Hour(3))), label);

        Assert.NotNull(cell.SparkPath);
        Assert.Equal(tone, cell.SparkTone);
    }

    [Theory]
    [InlineData("Spread bps")]
    [InlineData("Funding")]
    [InlineData("Open interest")]
    [InlineData("Depth 25bps")]
    public void None_of_them_is_drawn_at_full_strength(string label)
    {
        // Not a default and not an oversight: the rendered ui_kit carries thirty-nine sparklines and
        // uses --spark-ticker-hot on exactly one per row, --spark-oi-hot and --spark-depth-hot on
        // none. Full strength is the loud note in a column of lines and it stays rare, on rule 6's
        // argument about acid.
        Assert.False(Cell(Cells(Series(Hour(0), Hour(1), Hour(2), Hour(3))), label).SparkHot);
    }

    [Fact]
    public void Depth_25bps_is_the_one_cell_that_carries_both_marks()
    {
        // Rule 11, verbatim: "depth 25bps carries both, the mirrored bar for the two sides and the
        // line for the hour." The other two bands carry the bar alone.
        var cells = Cells(Series(Hour(0), Hour(1), Hour(2), Hour(3)));

        var d25 = Cell(cells, "Depth 25bps");
        Assert.NotNull(d25.SparkPath);
        Assert.NotNull(d25.MirrorBidWidth);

        foreach (var band in new[] { "Depth 10bps", "Depth 50bps" })
        {
            Assert.Null(Cell(cells, band).SparkPath);
        }
    }

    [Fact]
    public void The_line_in_that_cell_is_drawn_at_the_height_the_mirror_leaves()
    {
        // Two marks, one history slot, so the cell that holds both is exactly as tall as the cell
        // that holds one and the row keeps its single figure line. The path's y values have to fall
        // inside the shorter box, or the viewBox and the box disagree and the browser silently
        // rescales the line.
        var d25 = Cell(Cells(Series(Hour(0, depthBid: 1, depthAsk: 1), Hour(1, depthBid: 900, depthAsk: 900))), "Depth 25bps");

        var ys = d25.SparkPath!.Split(' ')
            .Where(t => t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '-'))
            .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture))
            .Where((_, i) => i % 2 == 1)
            .ToList();

        Assert.NotEmpty(ys);
        Assert.All(ys, y => Assert.InRange(y, 0, Format.SplitSparkHeight));
    }

    // ── A gap stays a gap ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_hour_the_venue_has_no_row_for_breaks_the_line()
    {
        // The same argument CandleStore makes about bars: a venue that went dark must not have its
        // remaining points slide together and draw as an unbroken line. The path starts a new
        // subpath at the gap — a second `M` — instead of joining across it.
        var cells = Cells(Series(Hour(0), null, Hour(2), Hour(3)));

        Assert.Equal(2, Cell(cells, "Open interest").SparkPath!.Count(c => c == 'M'));
    }

    [Fact]
    public void A_metric_the_hour_could_not_measure_is_a_gap_and_never_a_zero()
    {
        // MetricHour.Aggregate writes null, not zero, when an hour held no valid measurement — a
        // crossed book leaves the spread out of its average. A zero drawn there would read as "the
        // spread was flat", which is a measurement, and the hour has none. Rule 8, one layer down.
        var cells = Cells(Series(
            Hour(0, spread: 5), Hour(1, spread: null), Hour(2, spread: 5), Hour(3, spread: 5)));

        Assert.Equal(2, Cell(cells, "Spread bps").SparkPath!.Count(c => c == 'M'));
    }

    [Fact]
    public void A_venue_with_no_hourly_rows_at_all_draws_no_line_rather_than_a_flat_one()
    {
        var cells = Cells(MetricHourSeries.Empty);

        foreach (var label in new[] { "Spread bps", "Funding", "Open interest", "Depth 25bps" })
        {
            Assert.Null(Cell(cells, label).SparkPath);
        }
    }

    // ── Depth is the two sides added, or it is nothing ──────────────────────────────────────────

    [Fact]
    public void The_depth_line_is_both_sides_of_the_book_together()
    {
        // The mirror already says how the book is split right now; the line's job is the other
        // question, how deep it was over the hour. That is the two halves of the mirror added up
        // rather than a third quantity.
        var series = Series(Hour(0, depthBid: 40, depthAsk: 60), Hour(1, depthBid: 1, depthAsk: 1));

        Assert.Equal([100d, 2d], series.Depth25);
    }

    [Fact]
    public void An_hour_with_one_side_missing_is_a_gap_and_not_the_side_that_is_there()
    {
        // Half a book plotted beside whole ones is a collapse the market did not have — the
        // strongest claim this column can make, made out of an absence of data.
        var series = Series(
            Hour(0, depthBid: 40, depthAsk: 60),
            Hour(1, depthBid: 40, depthAsk: null),
            Hour(2, depthBid: null, depthAsk: 60));

        Assert.Equal([100d, null, null], series.Depth25);
    }

    [Fact]
    public void An_hour_built_on_no_snapshot_is_not_drawn()
    {
        // snapshot_count is `not null` in the schema and the aggregate should never write a zero
        // there. If one ever appears, it is an hour averaged from nothing, and a point drawn from
        // nothing is the one thing this surface does not do.
        var kept = MetricHourStore.MinSnapshots;

        Assert.Equal(1, kept);
    }

    // ── One axis for all seven lines ────────────────────────────────────────────────────────────

    [Fact]
    public void The_metric_series_and_the_price_series_are_drawn_on_the_same_hours()
    {
        // Seven lines sit in adjacent columns of one row and are read across as if they shared a
        // time axis. They only do if the axis is literally one list. Two implementations that agree
        // today would drift the first time either changed, and the drift would show as nothing at
        // all: seven lines still drawn, still eleven pixels tall, no longer the same day.
        var now = new DateTimeOffset(2026, 9, 6, 14, 37, 12, TimeSpan.Zero);
        var windows = CandleStore.Windows(now);

        Assert.Equal(CandleStore.Hours, windows.Count);

        // The last window is the hour that has CLOSED, never the one in progress: the rollup only
        // writes closed rows, so asking for 14:00 at 14:37 asks for a row that will not exist for
        // another twenty-three minutes — and an empty trailing point on every series would read as
        // every venue going quiet at the same instant.
        Assert.Equal(new DateTime(2026, 9, 6, 13, 0, 0, DateTimeKind.Utc), windows[^1]);
        Assert.Equal(new DateTime(2026, 9, 5, 13, 0, 0, DateTimeKind.Utc), windows[0]);
    }

    [Fact]
    public void The_open_interest_line_is_not_multiplied_into_base_units()
    {
        // The figure above it is, and the line deliberately is not: a sparkline is normalised
        // between its own minimum and maximum, so a constant factor cannot move a pixel of it.
        // Multiplying here would be arithmetic done for appearances on a number nobody reads off
        // this line — and it would be a second place for the multiplier to be got wrong.
        var series = Series(Hour(0, oi: 3), Hour(1, oi: 9));

        Assert.Equal([3d, 9d], series.OpenInterest);
    }
}

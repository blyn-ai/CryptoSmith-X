using CryptoSmithX.MarketData.Api;

namespace CryptoSmithX.MarketData.Api.Tests;

/// <summary>
/// The hour grid's three states. The one that matters most is the difference between an hour before
/// a venue was connected and an hour after it with nothing kept: drawn alike, the day a venue was
/// added reads as a week-long outage.
/// </summary>
public sealed class CoverageHoursTests
{
    private static readonly DateTime From = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

    private static CoverageHours.HourRow Row(DateTime hour, int instruments = 2, long snapshots = 120,
        long? expected = 120, int? expectedKnown = null) =>
        new("okx-perp", hour, instruments, snapshots, expected, expectedKnown ?? instruments, 0, instruments);

    [Fact]
    public void An_hour_before_the_first_rolled_up_hour_was_not_collected_yet()
    {
        var first = From.AddHours(2);
        var rows = new Dictionary<DateTime, CoverageHours.HourRow> { [first] = Row(first) };

        var grid = CoverageHours.Grid(From, first, first, rows);

        Assert.Equal(
            [CoverageHours.NotCollectedYet, CoverageHours.NotCollectedYet, CoverageHours.Observed],
            grid.Select(c => c.State));
    }

    [Fact]
    public void An_hour_after_collection_began_with_no_row_was_not_observed()
    {
        var rows = new Dictionary<DateTime, CoverageHours.HourRow>
        {
            [From] = Row(From),
            [From.AddHours(2)] = Row(From.AddHours(2)),
        };

        var grid = CoverageHours.Grid(From, From.AddHours(2), From, rows);

        Assert.Equal(CoverageHours.NotObserved, grid[1].State);
        Assert.Null(grid[1].Instruments);
        Assert.Null(grid[1].Completeness);
    }

    [Fact]
    public void A_segment_with_no_rows_at_all_is_not_collected_yet_throughout()
    {
        var grid = CoverageHours.Grid(From, From.AddHours(3), null, new Dictionary<DateTime, CoverageHours.HourRow>());

        Assert.Equal(4, grid.Count);
        Assert.All(grid, c => Assert.Equal(CoverageHours.NotCollectedYet, c.State));
    }

    [Fact]
    public void Completeness_is_kept_over_expected()
    {
        var rows = new Dictionary<DateTime, CoverageHours.HourRow> { [From] = Row(From, snapshots: 90, expected: 120) };

        var cell = Assert.Single(CoverageHours.Grid(From, From, From, rows));

        Assert.Equal(0.75, cell.Completeness);
    }

    [Fact]
    public void Completeness_is_null_when_any_row_of_the_hour_lacks_an_expected_count()
    {
        // Two instruments, only one with expected_count: dividing by half the denominator would
        // report the hour twice as complete as it was.
        var rows = new Dictionary<DateTime, CoverageHours.HourRow>
        {
            [From] = Row(From, instruments: 2, snapshots: 120, expected: 60, expectedKnown: 1),
        };

        var cell = Assert.Single(CoverageHours.Grid(From, From, From, rows));

        Assert.Null(cell.Completeness);
        Assert.Null(cell.Expected);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("kraken-futures", 31, true)]
    [InlineData(null, 0, false)]
    [InlineData(null, 32, false)]
    [InlineData("DROP TABLE", 7, false)]
    public void Validate(string? segment, int? days, bool usable)
    {
        Assert.Equal(usable, CoverageHours.Validate(segment, days) is null);
    }
}

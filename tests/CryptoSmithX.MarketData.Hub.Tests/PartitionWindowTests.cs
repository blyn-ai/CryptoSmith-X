using CryptoSmithX.Database;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The one decision in retention that deletes data, pinned without a database.
///
/// Every test here is about a REFUSAL, because that is the direction in which this code can be
/// wrong invisibly: a partition kept too long costs disk, which an operator sees, while a partition
/// dropped too early costs observations nobody can buy back and nothing reports.
/// </summary>
public sealed class PartitionWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FortyEight = TimeSpan.FromHours(48);

    [Fact]
    public void A_month_partition_ends_on_the_first_of_the_next_month()
    {
        // Not the 30th at 23:59: the end is the first instant belonging to the NEXT partition,
        // which is what lets IsPast be a single comparison rather than a fencepost argument.
        Assert.Equal(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionWindow.RangeEndUtc("trade", "trade_2026_09"));

        Assert.Equal(
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionWindow.RangeEndUtc("trade", "trade_2026_12"));
    }

    [Fact]
    public void A_day_partition_ends_at_the_next_midnight()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero),
            PartitionWindow.RangeEndUtc("book_topn", "book_topn_2026_09_24"));

        // Across a month end, where naive arithmetic produces the 32nd.
        Assert.Equal(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionWindow.RangeEndUtc("trade", "trade_2026_09_30"));
    }

    [Fact]
    public void The_partition_being_written_to_is_never_past()
    {
        // Today's and yesterday's day partitions both still have rows inside the 48 h window.
        Assert.False(PartitionWindow.IsPast("trade", "trade_2026_10_06", Now, FortyEight));
        Assert.False(PartitionWindow.IsPast("trade", "trade_2026_10_05", Now, FortyEight));

        // And the month partition that holds today is not past either, however old its first rows —
        // the test is on the range's end, which is why a 48 h window cannot express itself through
        // monthly partitions at all.
        Assert.False(PartitionWindow.IsPast("trade", "trade_2026_10", Now, FortyEight));
    }

    [Fact]
    public void A_day_partition_is_past_only_once_all_of_it_is_older_than_the_window()
    {
        // Ends 2026-10-04T00:00Z; the cutoff is 2026-10-04T12:00Z. Past.
        Assert.True(PartitionWindow.IsPast("trade", "trade_2026_10_03", Now, FortyEight));

        // Ends 2026-10-05T00:00Z, which is INSIDE the window by twelve hours. Kept.
        Assert.False(PartitionWindow.IsPast("trade", "trade_2026_10_04", Now, FortyEight));
    }

    [Fact]
    public void A_partition_whose_range_ends_exactly_on_the_cutoff_is_past()
    {
        // The boundary spelled out rather than left to the reader: at this instant the cutoff is
        // exactly 2026-10-04T00:00Z, which is where the partition of the 3rd ends. The range is
        // half-open, so that partition holds nothing at or after the cutoff and goes.
        var midnight = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.True(PartitionWindow.IsPast("trade", "trade_2026_10_03", midnight, FortyEight));

        // Its neighbour, one second inside the window, stays — the off-by-one that would quietly
        // take a day's data early.
        Assert.False(PartitionWindow.IsPast("trade", "trade_2026_10_04", midnight, FortyEight));
    }

    [Fact]
    public void A_window_of_zero_keeps_everything_forever()
    {
        // Production's standing answer, and the default every contour ships with. Even a partition
        // from years ago stays.
        Assert.False(PartitionWindow.IsPast("trade", "trade_2020_01", Now, TimeSpan.Zero));
        Assert.False(PartitionWindow.IsPast("trade", "trade_2020_01_01", Now, TimeSpan.Zero));
        Assert.False(PartitionWindow.IsPast("trade", "trade_2020_01", Now, TimeSpan.FromHours(-5)));
    }

    [Theory]
    // Somebody's backup sitting beside the parent, and the shape this code must never read as a date.
    [InlineData("trade_old")]
    [InlineData("trade_backup_2026_09")]
    [InlineData("trade_2026")]
    [InlineData("trade_2026_9")]        // month not zero-padded: not a name this system writes
    [InlineData("trade_2026_09_3")]     // nor is a one-digit day
    [InlineData("trade_2026_13")]       // no such month
    [InlineData("trade_2026_02_30")]    // no such day in February
    [InlineData("trade_abcd_09")]
    [InlineData("trade_2026_09_24_extra")]
    public void A_name_this_system_does_not_write_is_never_droppable(string partition)
    {
        Assert.Null(PartitionWindow.RangeEndUtc("trade", partition));
        Assert.False(PartitionWindow.IsPast("trade", partition, Now, FortyEight));
    }

    [Fact]
    public void A_partition_of_a_different_parent_is_not_claimed()
    {
        // market_snapshot_2026_09 is not a partition of market_candle, and the prefix check is what
        // stops one table's retention from reaching into another's tree.
        Assert.Null(PartitionWindow.RangeEndUtc("market_candle", "market_snapshot_2026_09"));

        // The near-miss that a looser check would accept: one parent's name is a prefix of another's.
        Assert.Null(PartitionWindow.RangeEndUtc("trade", "trade_events_2026_09"));
    }

    [Fact]
    public void The_parent_itself_is_not_a_partition_of_itself()
    {
        Assert.Null(PartitionWindow.RangeEndUtc("trade", "trade"));
        Assert.False(PartitionWindow.IsPast("trade", "trade", Now, FortyEight));
    }
}

using System.Globalization;

namespace CryptoSmithX.Database;

/// <summary>
/// Which partition is entirely in the past, decided from its name alone — the one piece of retention
/// that can be wrong in a way nothing downstream would notice, so it is pure, separate and tested
/// without a database (<c>PartitionWindowTests</c>).
///
/// <b>Two rules, and both are about refusing rather than deleting.</b> A name this code cannot parse
/// is never droppable: partitions are created by <c>create_month_partition</c> and
/// <c>create_day_partition</c>, whose names are built from a format string, so anything else in the
/// partition tree was put there by a person for a reason nobody wrote down, and guessing at its
/// range is how an hour of someone's work disappears. And the test is on the range's UPPER bound:
/// a partition is past only once the whole of it is older than the window, so the one still being
/// written to is never a candidate, however old its first row.
/// </summary>
public static class PartitionWindow
{
    /// <summary>
    /// The instant a partition's range ends, read from its name, or null when the name is not one
    /// this system creates. <c>trade_2026_09</c> ends at 2026-10-01T00:00Z; <c>trade_2026_09_24</c>
    /// ends at 2026-09-25T00:00Z — in both cases the first instant that belongs to the NEXT
    /// partition, which is what makes the comparison in <see cref="IsPast"/> a simple one.
    /// </summary>
    /// <param name="parent">The partitioned table, whose name prefixes every partition of it.</param>
    public static DateTimeOffset? RangeEndUtc(string parent, string partition)
    {
        if (partition is null || parent is null || !partition.StartsWith(parent + "_", StringComparison.Ordinal))
        {
            return null;
        }

        var tail = partition[(parent.Length + 1)..].Split('_');
        if (!(tail.Length is 2 or 3)
            || !int.TryParse(tail[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(tail[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || tail[0].Length != 4 || tail[1].Length != 2
            || year is < 2000 or > 9999 || month is < 1 or > 12)
        {
            return null;
        }

        if (tail.Length == 2)
        {
            return new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        }

        if (!int.TryParse(tail[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || tail[2].Length != 2
            || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
    }

    /// <summary>
    /// Whether this partition's whole range is older than <paramref name="window"/> measured back
    /// from <paramref name="now"/>. A window of zero or less is "never delete" and answers false for
    /// everything — the default every contour ships with, and the standing rule on production.
    /// </summary>
    public static bool IsPast(string parent, string partition, DateTimeOffset now, TimeSpan window) =>
        window > TimeSpan.Zero
        && RangeEndUtc(parent, partition) is { } end
        && end <= now - window;
}

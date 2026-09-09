using Dapper;
using Npgsql;

namespace CryptoSmithX.Database;

/// <summary>
/// Month partitions for every range-partitioned table. The DDL ships
/// <c>create_month_partition</c> and is idempotent, so this is safe to call on every start and
/// before any write that could land in a month nobody has created yet.
/// </summary>
public static class Partitions
{
    /// <summary>
    /// Every table partitioned by month. The three from 0032 joined the two originals when their
    /// collectors were written: a partitioned table with no partition for the row's month does not
    /// write a row into a default partition, it fails the insert outright, so a table missing from
    /// this list is a collector that cannot write at all the moment the month turns.
    /// </summary>
    public static readonly string[] PartitionedTables =
        ["market_snapshot", "market_candle", "trade", "book_topn", "market_price_candle"];

    public static async Task EnsureAsync(NpgsqlConnection conn, DateTimeOffset anyTimeIn, CancellationToken ct)
    {
        // Passed as text and cast in SQL: Dapper has no parameter mapping for DateOnly, and a
        // DateTime would drag a time and a kind along for a value that is only ever a month.
        var month = anyTimeIn.UtcDateTime.ToString("yyyy-MM-01", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var table in PartitionedTables)
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    "select create_month_partition(@table::regclass, @month::date)",
                    new { table, month },
                    cancellationToken: ct));
        }
    }

    /// <summary>This month and next, which is all a service that only writes "now" ever needs.</summary>
    public static async Task EnsureCurrentAndNextAsync(Db db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenAsync(ct);
        await EnsureAsync(conn, now, ct);
        await EnsureAsync(conn, now.AddMonths(1), ct);
    }
}

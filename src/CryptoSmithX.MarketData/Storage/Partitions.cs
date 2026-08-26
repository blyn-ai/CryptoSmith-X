using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Storage;

/// <summary>
/// Month partitions for the two range-partitioned tables. The DDL ships
/// <c>create_month_partition</c> and is idempotent, so this is safe to call on every start and
/// before any write that could land in a month nobody has created yet.
/// </summary>
public static class Partitions
{
    public static readonly string[] PartitionedTables = ["market_snapshot", "market_candle"];

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

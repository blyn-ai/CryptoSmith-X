using Dapper;
using Npgsql;

namespace CryptoSmithX.Database;

/// <summary>
/// Range partitions for every partitioned table. The DDL ships <c>ensure_partition</c>, which picks
/// month or day from the <c>partition_granularity</c> setting and is idempotent, so this is safe to
/// call on every start and before any write that could land in a range nobody has created yet.
///
/// <b>The choice is made in SQL, not here, and that is the point.</b> Eight places in the Hub create
/// partitions. If the granularity were decided in C#, two of them could disagree — and a month
/// partition laid over day partitions is not a cosmetic difference but an overlapping range, which
/// Postgres answers by refusing the insert. One function in the database cannot disagree with
/// itself; see the 0068 migration header.
/// </summary>
public static class Partitions
{
    /// <summary>
    /// Every table partitioned by range. The three from 0032 joined the two originals when their
    /// collectors were written: a partitioned table with no partition for the row's range does not
    /// write a row into a default partition, it fails the insert outright, so a table missing from
    /// this list is a collector that cannot write at all the moment the range turns.
    /// </summary>
    public static readonly string[] PartitionedTables =
        ["market_snapshot", "market_candle", "trade", "book_topn", "market_price_candle"];

    /// <summary>The partition covering <paramref name="anyTimeIn"/>, at whatever granularity the
    /// database is configured for. Used by the collectors, including on the backfill path, which
    /// easily reaches into a range nobody has created yet.</summary>
    public static async Task EnsureAsync(NpgsqlConnection conn, DateTimeOffset anyTimeIn, CancellationToken ct)
    {
        foreach (var table in PartitionedTables)
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    "select ensure_partition(@table::regclass, @at)",
                    new { table, at = anyTimeIn.UtcDateTime },
                    cancellationToken: ct));
        }
    }

    /// <summary>
    /// The partition for now and the one after it — all a service that only writes "now" ever needs,
    /// and what carries it across the turn of a day or a month. What "the one after" means follows
    /// the same setting the creation does, so a daily contour does not quietly stock a partition a
    /// month out.
    /// </summary>
    public static async Task EnsureCurrentAndNextAsync(Db db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenAsync(ct);
        foreach (var table in PartitionedTables)
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    "select ensure_next_partitions(@table::regclass, @at)",
                    new { table, at = now.UtcDateTime },
                    cancellationToken: ct));
        }
    }
}

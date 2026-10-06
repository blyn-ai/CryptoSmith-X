using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Retention;

/// <summary>
/// Keeps partitions ahead of the writers, and — only where an operator has asked for it — drops the
/// ones whose whole range has aged out.
///
/// <b>Deletion is off unless the contour says otherwise, and that is deliberate.</b> A snapshot
/// carries spread, book depth and open interest at an instant, and no venue sells those back at any
/// price: on production the answer stays "keep", which is what <c>retention_delete_after_hours = 0</c>
/// means and what every contour gets by default. A short-lived contour is the opposite case — the
/// test rig exists to prove the code runs, not to hold history, and in September it filled its disk
/// and sat dead for twelve days because nothing here could delete. So the window is a number in the
/// database, 0 everywhere until a person writes otherwise.
///
/// <b>What it drops is partitions, never rows.</b> A DELETE returns nothing to the filesystem, and
/// VACUUM FULL needs a second copy of the table — which is precisely the space a contour in this
/// state does not have. Dropping the partition returns the files to the disk immediately, which is
/// why the window below is only expressible at the granularity the partitions are cut at
/// (<c>partition_granularity</c>, 0068).
/// </summary>
public sealed class RetentionJob
{
    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetentionJob> _logger;

    public RetentionJob(DbSettings settings, Db db, TimeProvider clock, ILogger<RetentionJob> logger)
    {
        _settings = settings;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Returns the number of partitions dropped.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // collector_run used to be trimmed to a week, on the reasoning that the UI only needs
        // a recent list. That reasoning was backwards: this is the layer that says whether an
        // observation is missing because the market was quiet or because we were blind, and
        // that question gets asked about last month, not about this hour. It is also cheap —
        // tens of thousands of rows a day against millions of snapshots. Nothing deletes it
        // now; the rework decides retention from measured volumes, and the default is keep.
        var now = _clock.GetUtcNow();
        await using var conn = await _db.OpenAsync(ct);

        // Before anything is considered for dropping: the ranges being written to have to exist.
        // Order matters on a contour whose window is short — a job that dropped first and created
        // second would, for the length of one statement, leave the writers with nowhere to put a row.
        await Partitions.EnsureAsync(conn, now, ct);
        await Partitions.EnsureAsync(conn, now.AddMonths(1), ct);

        var window = (await _settings.CurrentAsync(ct)).RetentionDeleteAfter;
        if (window <= TimeSpan.Zero)
        {
            return 0;
        }

        var dropped = 0;
        foreach (var parent in Partitions.PartitionedTables)
        {
            // Only the real children of this parent, read from the catalogue rather than guessed by
            // name match — a table called market_snapshot_old sitting beside the parent is somebody's
            // backup, not a partition, and nothing here may touch it.
            var names = await conn.QueryAsync<string>(new CommandDefinition(
                """
                select c.relname
                  from pg_class c
                  join pg_inherits h on h.inhrelid = c.oid
                  join pg_class p on p.oid = h.inhparent
                 where p.relname = @parent
                """,
                new { parent }, cancellationToken: ct));

            foreach (var name in names)
            {
                if (!PartitionWindow.IsPast(parent, name, now, window))
                {
                    continue;
                }

                // Quoted even though the name came from pg_class and matched a strict shape above:
                // an identifier reaching DDL by interpolation should not rest on one check alone.
                await conn.ExecuteAsync(new CommandDefinition(
                    $"drop table if exists {Quote(name)}", cancellationToken: ct));
                dropped++;

                _logger.LogInformation(
                    "Retention dropped partition {Partition}: its range ends before {Cutoff:u}, the "
                    + "{Hours} h window set by retention_delete_after_hours",
                    name, now - window, window.TotalHours);
            }
        }

        return dropped;
    }

    /// <summary>
    /// The name comes from pg_class and is already matched against a strict pattern above; quoting
    /// keeps the interpolation honest rather than relying on that alone.
    /// </summary>
    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

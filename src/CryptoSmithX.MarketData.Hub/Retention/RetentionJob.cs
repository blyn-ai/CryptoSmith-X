using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Retention;

/// <summary>
/// Keeps partitions ahead of the writers and drops snapshot history past the retention window.
/// Candles are never dropped — they are the only source for long backtests.
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

        await Partitions.EnsureAsync(conn, now, ct);
        await Partitions.EnsureAsync(conn, now.AddMonths(1), ct);

        // Retention for 'snapshot' is collection-level only, never per-exchange: market_snapshot
        // partitions hold every exchange's rows for a month at once, so dropping one cannot spare a
        // single exchange even if its exchange_collection.retention_days says otherwise (see the
        // 0014 migration header). A collection whose retention is null never rotates — 'snapshot'
        // always has one, but the null-guard keeps this job honest if that default is ever cleared.
        var retentionDays = (await _settings.CurrentAsync(ct)).CollectionRetentionDays("snapshot");
        if (retentionDays is null)
        {
            return 0;
        }

        // A month is droppable once its last day is older than the window, so a partial month is
        // never taken away early.
        var cutoff = now.AddDays(-retentionDays.Value);
        var names = await conn.QueryAsync<string>(new CommandDefinition(
            """
            select c.relname
              from pg_class c
              join pg_inherits h on h.inhrelid = c.oid
              join pg_class p on p.oid = h.inhparent
             where p.relname = 'market_snapshot'
               and c.relname ~ '^market_snapshot_[0-9]{4}_[0-9]{2}$'
            """,
            cancellationToken: ct));

        var dropped = 0;
        foreach (var name in names)
        {
            // market_snapshot_YYYY_MM — take the tail rather than counting characters.
            var parts = name.Split('_');
            var year = int.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture);
            var month = int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture);
            var endOfMonth = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

            if (endOfMonth > cutoff)
            {
                continue;
            }

            _logger.LogInformation("Dropping snapshot partition {Partition}", name);
            await conn.ExecuteAsync(new CommandDefinition(
                $"drop table if exists {Quote(name)}", cancellationToken: ct));
            dropped++;
        }

        return dropped;
    }

    /// <summary>
    /// The name comes from pg_class and is already matched against a strict pattern above; quoting
    /// keeps the interpolation honest rather than relying on that alone.
    /// </summary>
    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

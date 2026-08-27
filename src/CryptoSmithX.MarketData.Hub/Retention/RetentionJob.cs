using CryptoSmithX.MarketData.Hub.Options;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Retention;

/// <summary>
/// Keeps partitions ahead of the writers and drops snapshot history past the retention window.
/// Candles are never dropped — they are the only source for long backtests.
/// </summary>
public sealed class RetentionJob
{
    private readonly MarketDataOptions _options;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetentionJob> _logger;

    public RetentionJob(MarketDataOptions options, Db db, TimeProvider clock, ILogger<RetentionJob> logger)
    {
        _options = options;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Returns the number of partitions dropped.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var conn = await _db.OpenAsync(ct);

        await Partitions.EnsureAsync(conn, now, ct);
        await Partitions.EnsureAsync(conn, now.AddMonths(1), ct);

        // A month is droppable once its last day is older than the window, so a partial month is
        // never taken away early.
        var cutoff = now.AddDays(-_options.SnapshotRetentionDays);
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

using System.Reflection;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Storage;

/// <summary>
/// Applies the embedded .sql files in name order, once. The whole run is wrapped in a session-level
/// advisory lock so two instances starting together cannot both apply 0001.
/// </summary>
public static class Migrator
{
    private const long AdvisoryLockKey = 8_534_221_907_001L;

    public static async Task RunAsync(Db db, ILogger logger, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("select pg_advisory_lock(@key)", new { key = AdvisoryLockKey });
        try
        {
            await conn.ExecuteAsync(
                "create table if not exists schema_version (version int primary key, applied_at timestamptz not null default now())");

            var applied = (await conn.QueryAsync<int>("select version from schema_version")).ToHashSet();

            foreach (var (version, name, sql) in Load())
            {
                if (applied.Contains(version))
                {
                    continue;
                }

                logger.LogInformation("Applying migration {Version} {Name}", version, name);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync(
                    "insert into schema_version (version) values (@version)", new { version }, tx);
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await conn.ExecuteAsync("select pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    private static IEnumerable<(int Version, string Name, string Sql)> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resource in names)
        {
            var file = resource[(resource.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            var version = int.Parse(file[..file.IndexOf('_', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);

            using var stream = asm.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource {resource} could not be opened.");
            using var reader = new StreamReader(stream);
            yield return (version, file, reader.ReadToEnd());
        }
    }
}

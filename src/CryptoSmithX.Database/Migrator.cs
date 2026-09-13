using System.Reflection;
using Dapper;
using Npgsql;

namespace CryptoSmithX.Database;

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

    /// <summary>
    /// Read-only check for the processes that no longer migrate. Throws when <c>schema_version</c>
    /// is missing or behind the embedded set. Compose ordering already makes migration a
    /// precondition; this turns a mis-start into a loud failure instead of a puzzling one.
    /// </summary>
    public static async Task VerifyAsync(Db db, CancellationToken ct)
    {
        var expected = Load().Select(m => m.Version).ToHashSet();

        await using var conn = await db.OpenAsync(ct);

        var hasTable = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select to_regclass('public.schema_version') is not null", cancellationToken: ct));
        if (!hasTable)
        {
            throw new InvalidOperationException(
                "schema_version is missing: run CryptoSmithX.Database to apply migrations before starting this service.");
        }

        var applied = (await conn.QueryAsync<int>(
            new CommandDefinition("select version from schema_version", cancellationToken: ct))).ToHashSet();
        var missing = expected.Where(v => !applied.Contains(v)).OrderBy(v => v).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Database schema is behind: migration(s) {string.Join(", ", missing)} not applied. "
                + "Run CryptoSmithX.Database to migrate.");
        }
    }

    /// <summary>
    /// The embedded migrations, in name order, with their numbers.
    ///
    /// <b>Two files may not share a number, and this refuses to start rather than picking one.</b>
    /// What is applied is recorded as an INT, so a second 0057 would find its number already in
    /// schema_version and be skipped — no error, no log line, and whatever it was meant to change
    /// simply never happens. That is not hypothetical: two branches landed a 0057 the same
    /// afternoon, and the one that sorted second was a base_url the segment would have run without.
    ///
    /// Refusing at startup is the right severity. A duplicate number is a merge accident with a
    /// one-character fix, and the alternative is a deployment that looks healthy while carrying a
    /// change nobody applied.
    /// </summary>
    /// <summary>
    /// The version and file name of every embedded migration, in the order they would be applied.
    ///
    /// Public so the rule below can be checked by a test rather than only at startup: a duplicate
    /// number is a merge accident, and finding it in CI costs a rename while finding it in a deploy
    /// costs a change nobody notices is missing.
    /// </summary>
    public static IReadOnlyList<(int Version, string Name)> EmbeddedVersions() =>
        Read().Select(m => (m.Version, m.Name)).ToList();

    private static IReadOnlyList<(int Version, string Name, string Sql)> Load()
    {
        var loaded = Read().ToList();

        var clash = loaded
            .GroupBy(m => m.Version)
            .FirstOrDefault(g => g.Count() > 1);

        if (clash is not null)
        {
            throw new InvalidOperationException(
                $"Two migrations share the version {clash.Key}: {string.Join(", ", clash.Select(m => m.Name))}. "
                + "Only one of them would ever be applied, and silently — renumber the later one.");
        }

        return loaded;
    }

    private static IEnumerable<(int Version, string Name, string Sql)> Read()
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

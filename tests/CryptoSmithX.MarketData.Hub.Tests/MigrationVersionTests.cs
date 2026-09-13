using CryptoSmithX.Database;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The embedded migrations, checked for the one property nothing else would notice was broken.
/// </summary>
public sealed class MigrationVersionTests
{
    [Fact]
    public void No_two_migrations_share_a_version()
    {
        // What is applied is recorded as an INT, so a second 0057 finds its number already in
        // schema_version and is SKIPPED — no error, no log line, and whatever it was meant to change
        // simply never happens. Two branches landed a 0057 the same afternoon; the one that sorted
        // second set a base_url the segment would otherwise have run without, and nothing anywhere
        // would have said so.
        //
        // Migrator refuses to start on a duplicate. This is the same rule, found in CI, where the
        // fix is a rename rather than a deployment that looks healthy.
        var duplicates = Migrator.EmbeddedVersions()
            .GroupBy(m => m.Version)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(m => m.Name))}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_migration_is_numbered_and_named()
    {
        // The loader parses the number off the file name up to the first underscore, so a file
        // without one takes the whole name to int.Parse and brings the process down at startup.
        var all = Migrator.EmbeddedVersions();

        Assert.NotEmpty(all);
        Assert.All(all, m =>
        {
            Assert.True(m.Version > 0, $"{m.Name} has no positive version");
            Assert.Contains("_", m.Name, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_order_they_apply_in_is_the_order_of_their_numbers()
    {
        // They are loaded in NAME order and numbered in the same sequence, so the two orders agree —
        // and a file numbered out of step with its name would apply before something it depends on.
        var versions = Migrator.EmbeddedVersions().Select(m => m.Version).ToList();

        Assert.Equal(versions.Order(), versions);
    }
}

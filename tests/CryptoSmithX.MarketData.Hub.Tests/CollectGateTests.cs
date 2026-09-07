using System.Text.RegularExpressions;
using CryptoSmithX.MarketData.Hub.Ingestion;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The collect gate (0029): a listing seen for the first time arrives switched off unless its base
/// asset is auto-approved, and no later discovery pass ever writes <c>collect</c> again.
///
/// The gate is two halves and each is tested against the half that carries it. WHAT the value is on
/// arrival is a pure decision — <see cref="DiscoveryCollector.DecideCollectOnInsert"/> — driven
/// directly below. WHERE that value is allowed to land is a property of one SQL statement: collect
/// appears in the insert list and nowhere in the <c>do update set</c> list, so PostgreSQL cannot
/// change it on a conflict. That is not a proxy for "an operator's decision survives a pass" — it
/// is the entire mechanism, and reading it off the statement is the same proof the database
/// performs. So this file follows CollectFilterTests: it guards the SQL a refactor could silently
/// change, without needing a database in CI. End-to-end behaviour against a real postgres:16 was
/// run by hand and is in the change's notes.
/// </summary>
public sealed class CollectGateTests
{
    // The 25 the owner approved, as far as these tests care: what matters is membership, not which.
    private static readonly IReadOnlySet<string> Approved =
        new HashSet<string>(["BTC", "ETH", "SOL", "PEPE", "ONDO"], StringComparer.Ordinal);

    // Everything after "do update set". Anything mentioned here is rewritten on every pass.
    private static string UpdateList =>
        DiscoveryCollector.UpsertInstrumentSql[
            (DiscoveryCollector.UpsertInstrumentSql.IndexOf("do update set", StringComparison.Ordinal)
             + "do update set".Length)..];

    private static string InsertHalf =>
        DiscoveryCollector.UpsertInstrumentSql[
            ..DiscoveryCollector.UpsertInstrumentSql.IndexOf("do update set", StringComparison.Ordinal)];

    [Fact]
    public void An_auto_approved_asset_arrives_collected()
    {
        Assert.True(DiscoveryCollector.DecideCollectOnInsert("BTC", Approved));

        // …and the decision reaches the row: collect is a column of the insert, bound to @Collect.
        Assert.Matches(new Regex(@"\bcollect\b\)", RegexOptions.None), InsertHalf);
        Assert.Contains("@Collect", InsertHalf, StringComparison.Ordinal);
    }

    [Fact]
    public void An_asset_off_the_list_arrives_uncollected_and_reads_as_new()
    {
        Assert.False(DiscoveryCollector.DecideCollectOnInsert("MOODENG", Approved));

        // NEW is collect = false AND collect_changed_at is null (0029). Discovery never writes the
        // audit columns — nothing in the statement mentions them — so a row it inserts false is
        // undecided by construction, not merely off. If a future edit made discovery stamp
        // collect_changed_at, an arrival would masquerade as a human's decision and vanish from the
        // NEW queue nobody has looked at yet.
        Assert.DoesNotContain("collect_changed_at", DiscoveryCollector.UpsertInstrumentSql, StringComparison.Ordinal);
        Assert.DoesNotContain("collect_changed_by", DiscoveryCollector.UpsertInstrumentSql, StringComparison.Ordinal);
        Assert.DoesNotContain("collect_note", DiscoveryCollector.UpsertInstrumentSql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pass_over_an_instrument_an_operator_switched_off_leaves_it_off()
    {
        // The 1864 rows an admin has just switched off are the live case. collect is absent from
        // the update list, so `on conflict do update` has no assignment that could raise it — for
        // an approved asset as much as any other.
        Assert.DoesNotMatch(new Regex(@"^\s*collect\s*=", RegexOptions.Multiline), UpdateList);
    }

    [Fact]
    public void A_pass_over_an_instrument_an_operator_switched_on_leaves_it_on()
    {
        // Same single guarantee, the other direction — and the one that would break first if the
        // gate were "helpfully" made consistent, because a re-stamp would write the default false
        // over every operator's yes on an unapproved asset.
        Assert.DoesNotMatch(new Regex(@"^\s*collect\s*=", RegexOptions.Multiline), UpdateList);

        // The conflict must still take the UPDATE branch: `do nothing` would drop the venue's own
        // fields (status, last_seen_at) and quietly stop delisting from ever being noticed.
        Assert.Contains("on conflict (segment_code, exchange_symbol) do update set",
            DiscoveryCollector.UpsertInstrumentSql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_consecutive_pass_changes_nothing()
    {
        // Nothing about collect depends on the pass: the decision is a pure function of the canon
        // and the approved set, so pass two computes what pass one did…
        Assert.Equal(
            DiscoveryCollector.DecideCollectOnInsert("BTC", Approved),
            DiscoveryCollector.DecideCollectOnInsert("BTC", Approved));

        // …and pass two takes the update path, where collect is not written at all. Belt and
        // braces: the whole update list mentions collect exactly zero times, in any shape.
        Assert.DoesNotMatch(new Regex(@"\bcollect\b"), UpdateList[..UpdateList.IndexOf("-- collect is ABSENT", StringComparison.Ordinal)]);
    }

    [Fact]
    public void The_approved_set_is_matched_exactly_as_the_foreign_key_matches()
    {
        // asset.code is a case-sensitive text primary key and base_asset's FK points at one row.
        // 'btc' is a different asset from 'BTC'; approving one must not approve the other.
        Assert.False(DiscoveryCollector.DecideCollectOnInsert("btc", Approved));

        Assert.Contains("where auto_collect", DiscoveryCollector.AutoCollectAssetsSql, StringComparison.Ordinal);
    }
}

using CryptoSmithX.Arena.Data;

namespace CryptoSmithX.Arena.Tests;

/// <summary>
/// The guards every public query has to carry. There is no database in this test run, so the queries
/// themselves are the thing under test — which is the point: each of these lines is one somebody
/// could delete while tidying, and every one of them was omitted by at least one of the three
/// architecture proposals this work was assembled from.
///
/// A text assertion is a weak test and is not pretending otherwise. It cannot tell a correct join
/// from a wrong one. It can tell that a guard is gone.
/// </summary>
public sealed class PublicQueryTests
{
    public static TheoryData<string, string> PublicQueries() => new()
    {
        { nameof(ArenaStore.PairsSql), ArenaStore.PairsSql },
        { nameof(ArenaStore.PairVenuesSql), ArenaStore.PairVenuesSql },
        { nameof(SegmentFreshnessStore.Sql), SegmentFreshnessStore.Sql }
    };

    [Theory]
    [MemberData(nameof(PublicQueries))]
    public void Nothing_uncollected_is_published(string name, string sql) =>
        Assert.True(sql.Contains("i.collect", StringComparison.Ordinal), name);

    [Theory]
    [MemberData(nameof(PublicQueries))]
    public void An_instrument_in_halt_stays_in_the_comparison_carrying_its_state(string name, string sql)
    {
        // `<> 'delisted'`, never `= 'trading'`. halted, post_only and reduce_only belong on the page
        // with their mark; disappearing without one is the same lie as a zero standing in for a dash.
        Assert.True(sql.Contains("i.status <> 'delisted'", StringComparison.Ordinal), name);
        Assert.False(sql.Contains("i.status = 'trading'", StringComparison.Ordinal), name);
    }

    [Theory]
    [MemberData(nameof(PublicQueries))]
    public void The_development_venue_is_not_published(string name, string sql)
    {
        // 0002 seeds an in-process exchange called Fake, and 0005 leaves it enabled. Without this
        // line the front page of a site whose whole claim is "we do not invent data" carries a
        // platform called "Fake".
        Assert.True(sql.Contains("x.code <> 'fake'", StringComparison.Ordinal), name);
    }

    [Theory]
    [MemberData(nameof(PublicQueries))]
    public void The_public_surface_reads_latest_and_never_the_history_partition(string name, string sql)
    {
        // Blueprint §3, and 0025 turns it into a permission by withholding the grant: PairStore's
        // lateral over 26 million rows is the query an anonymous visitor can run in a loop.
        Assert.False(sql.Contains("from market_snapshot ", StringComparison.Ordinal), name);
        Assert.False(sql.Contains("from market_snapshot\n", StringComparison.Ordinal), name);
    }

    [Theory]
    [MemberData(nameof(PublicQueries))]
    public void No_query_reads_a_table_the_public_role_cannot_see(string name, string sql)
    {
        // Everything 0025 deliberately withholds. A query naming one of these does not run slowly in
        // production — it fails outright under arena_reader, and it should fail here first.
        //
        // `market_metric_hour` came off this list with 0026, and it is the only one that ever will
        // on this argument: its exclusion in 0025 was a sentence about what the pair page shows
        // ("агрегаты, которых страница пары не показывает") and rule 11 names those four columns.
        // The rest are not claims about the page — 26 million rows behind a lateral, credentials,
        // and the collector's report about itself standing in for the age of the data — and each of
        // them survives the page unchanged.
        foreach (var forbidden in new[]
                 {
                     "asset_alias", "funding_rate_history", "collector_status", "collector_run",
                     "collector_gap", "market_snapshot_hist", "webapp_user", "setting"
                 })
        {
            Assert.False(sql.Contains(forbidden, StringComparison.Ordinal), $"{name} reads {forbidden}");
        }
    }

    [Fact]
    public void The_fold_is_a_left_join_read_through_coalesce_and_never_an_inner_one()
    {
        // Absence of a membership row means "family = the asset itself" (0024). An inner join here
        // silently drops every pair nobody has folded yet — which is most of them — with no row, no
        // dash and no mark. One of the rejected proposals had exactly that on the quote side.
        Assert.Contains("left join asset_family_member bm", ArenaStore.PairsSql, StringComparison.Ordinal);
        Assert.Contains("left join asset_family_member qm", ArenaStore.PairsSql, StringComparison.Ordinal);
        Assert.Contains("coalesce(bm.family_code, i.base_asset)", ArenaStore.PairsSql, StringComparison.Ordinal);
        Assert.Contains("coalesce(qm.family_code, i.quote_asset)", ArenaStore.PairsSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fold_applies_to_both_halves_of_the_pair()
    {
        // Nothing in 0024 makes membership quote-only, and a WBTC/BTC family is the obvious next
        // entry an operator makes. A fold that worked on one side would break the day they made it.
        Assert.Contains("family_code = @baseFamily", ArenaStore.PairVenuesSql, StringComparison.Ordinal);
        Assert.Contains("family_code = @quoteFamily", ArenaStore.PairVenuesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unfolded_asset_is_still_addressable_as_its_own_family()
    {
        // The expansion of family F is "the members of F, plus F itself when F is not folded into
        // something else" — the exact inverse of coalesce(member.family_code, asset). Dropping the
        // second arm would make a family BTC that holds only WBTC swallow plain BTC listings.
        Assert.Contains(
            "where not exists (select 1 from asset_family_member where asset_code = @baseFamily)",
            ArenaStore.PairVenuesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_comparison_row_reports_the_venues_own_quote_and_not_the_family()
    {
        // The heading folds; the row never does. A row printing the folded quote would be claiming
        // Kraken quotes in USDT.
        var selectList = ArenaStore.PairVenuesSql[..ArenaStore.PairVenuesSql.IndexOf("from exchange_instrument", StringComparison.Ordinal)];
        Assert.Matches("i\\.base_asset\\s+as \"BaseAsset\"", selectList);
        Assert.Matches("i\\.quote_asset\\s+as \"QuoteAsset\"", selectList);
        Assert.DoesNotContain("coalesce", selectList, StringComparison.Ordinal);
    }

    [Fact]
    public void The_snapshot_join_is_left_so_an_unobserved_listing_can_say_so()
    {
        Assert.Contains("left join market_snapshot_latest s", ArenaStore.PairVenuesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void No_age_is_computed_in_sql()
    {
        // Absolute instants only. An age baked into the payload would be the age at the moment the
        // cache filled, and every cached response would then understate how old it is (blueprint §5).
        Assert.DoesNotContain("now()", ArenaStore.PairVenuesSql, StringComparison.Ordinal);
        Assert.DoesNotContain("current_timestamp", ArenaStore.PairVenuesSql, StringComparison.Ordinal);
    }
}

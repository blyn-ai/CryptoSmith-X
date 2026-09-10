using System.Data.Common;
using CryptoSmithX.WebApp.Studio.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Studio.Data;

/// <summary>
/// What a pair card's disclosure needs beyond the market: the alias registry behind the fold (A3)
/// and the collect decision on every listing (A4). Both are keyed on the same asset-family
/// expansion <see cref="StudioStore.PairVenuesSql"/> uses, for the same reason argued there — the
/// family is a SET of asset codes, and the two rejected shortcuts (a direct equality on the family
/// code, or an inner join through the fold table) both drop members silently.
/// </summary>
public static class AssetRegistryStore
{
    /// <summary>
    /// Every raw spelling that resolves into this family, from every venue. <c>coalesce</c> on
    /// <c>segment_code</c> is deliberately NOT applied here — the view prints null as "all venues"
    /// itself, so a caller reading the row directly still sees a real null rather than a string that
    /// happens to say the same thing. 0019 renamed <c>asset_alias.exchange_code</c> to
    /// <c>segment_code</c> along with every other collector-facing table — the alias is keyed to the
    /// adapter (segment), not the venue-level company row.
    /// </summary>
    public const string AliasesSql =
        """
        with base_codes as (
            select m.asset_code from asset_family_member m where m.family_code = @baseFamily
            union
            select @baseFamily
             where not exists (select 1 from asset_family_member where asset_code = @baseFamily)
        )
        select a.segment_code                 as "SegmentCode",
               a.alias                         as "Alias",
               a.asset_code                    as "AssetCode",
               a.multiplier::double precision  as "Multiplier",
               a.note                          as "Note"
          from asset_alias a
         where a.asset_code in (select asset_code from base_codes)
         order by a.segment_code nulls first, a.alias
        """;

    /// <summary>
    /// The gate 0029 set: whether a NEW listing of this asset arrives collecting or waiting. One
    /// row per family member, not folded to a single answer — a family can hold members with
    /// different flags (the identity member true, an alias false), and averaging them into one
    /// boolean would state a policy that does not exist.
    /// </summary>
    public const string AutoCollectSql =
        """
        with base_codes as (
            select m.asset_code from asset_family_member m where m.family_code = @baseFamily
            union
            select @baseFamily
             where not exists (select 1 from asset_family_member where asset_code = @baseFamily)
        )
        select bool_and(auto_collect) as "AllAutoCollect", bool_or(auto_collect) as "AnyAutoCollect"
          from asset where code in (select asset_code from base_codes)
        """;

    /// <summary>
    /// The collect decision on every listing this family has, whatever it decided — printed for a
    /// listing that already collects as much as for one waiting, because a card that only showed the
    /// waiting ones would be a different, narrower claim than "here is this family's audit trail".
    /// <c>ChangedAt</c>/<c>ChangedBy</c> are both null on a listing nobody ever had to decide: the
    /// gate wrote <c>collect</c> once, at INSERT, and a silent yes leaves no signature.
    /// </summary>
    public const string ListingsSql =
        """
        with base_codes as (
            select m.asset_code from asset_family_member m where m.family_code = @baseFamily
            union
            select @baseFamily
             where not exists (select 1 from asset_family_member where asset_code = @baseFamily)
        )
        select i.segment_code       as "SegmentCode",
               i.exchange_symbol    as "Symbol",
               i.status             as "Status",
               i.collect            as "Collect",
               i.collect_note       as "Note",
               i.collect_changed_at as "ChangedAt",
               i.collect_changed_by as "ChangedBy"
          from exchange_instrument i
          join segment sg on sg.code = i.segment_code
          join exchange x on x.code = sg.exchange_code
         where i.base_asset in (select asset_code from base_codes)
           and i.status <> 'delisted'
           and sg.status = 'enabled'
           and x.code <> 'fake'
         order by i.collect, i.segment_code, i.exchange_symbol
        """;

    public static async Task<AssetRegistryDetail> DetailAsync(
        DbConnection conn, string baseFamily, CancellationToken ct)
    {
        var aliases = (await conn.QueryAsync<AssetAliasRow>(new CommandDefinition(
            AliasesSql, new { baseFamily }, cancellationToken: ct))).ToList();

        var flags = await conn.QuerySingleOrDefaultAsync<(bool? AllAutoCollect, bool? AnyAutoCollect)>(
            new CommandDefinition(AutoCollectSql, new { baseFamily }, cancellationToken: ct));

        // Mixed within the family: neither "on" nor "off" is honest, so the view is told there is no
        // single answer rather than being handed one flag that silently picked a side.
        bool? autoCollect = flags.AllAutoCollect == flags.AnyAutoCollect ? flags.AllAutoCollect : null;

        var listings = (await conn.QueryAsync<CollectAuditRow>(new CommandDefinition(
            ListingsSql, new { baseFamily }, cancellationToken: ct))).ToList();

        return new AssetRegistryDetail(aliases, autoCollect, listings);
    }

    /// <summary>
    /// The three instrument-level counts A4's footer sentence needs. INSTRUMENTS, not families:
    /// <c>collect</c> is a fact about one listing, and a family-level count would have to pick a
    /// rule for a mixed family that does not exist anywhere else on this page.
    /// </summary>
    public const string CollectCountsSql =
        """
        select count(*) filter (where i.collect)     as "Collecting",
               count(*) filter (where not i.collect)  as "Waiting"
          from exchange_instrument i
          join segment sg on sg.code = i.segment_code
          join exchange x on x.code = sg.exchange_code
         where i.status <> 'delisted'
           and sg.status = 'enabled'
           and x.code <> 'fake'
        """;

    /// <summary>Blacklisted symbols never reach <c>exchange_instrument</c> at all — discovery drops
    /// them before the first insert (see <c>DiscoveryCollector</c>) — so there is no row to count
    /// them from. What IS countable is the venues' own declared lists, summed across every enabled
    /// segment.</summary>
    public const string BlacklistedSql =
        """
        select coalesce(sum(cardinality(blacklist)), 0)::int
          from segment where status = 'enabled'
        """;

    public static async Task<CollectCounts> CollectCountsAsync(DbConnection conn, CancellationToken ct)
    {
        var counts = await conn.QuerySingleAsync<(int Collecting, int Waiting)>(
            new CommandDefinition(CollectCountsSql, cancellationToken: ct));

        var blacklisted = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(BlacklistedSql, cancellationToken: ct));

        return new CollectCounts(counts.Collecting, counts.Waiting, blacklisted);
    }
}

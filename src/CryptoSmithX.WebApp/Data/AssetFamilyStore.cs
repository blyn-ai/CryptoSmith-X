using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// Asset families — the display-only fold introduced by 0024, edited next to the aliases it sits
/// above. An alias says "this venue spells BTC as XBT"; a family says "for the purpose of one
/// heading, USD and USDT are the same quote". Nothing here rewrites market data: membership only
/// decides which rows get drawn together, and every row keeps showing its venue's real quote.
///
/// Membership is optional, so every read of it elsewhere is a LEFT JOIN and a coalesce onto the
/// asset itself. Reads here are the opposite direction — the registry as the operator edits it —
/// so they start from the family and are inner by construction.
///
/// Writes return an error string rather than throwing, the way <see cref="AssetStore"/> does, so a
/// bad code becomes an alert on the page instead of a 500.
/// </summary>
public static class AssetFamilyStore
{
    public static async Task<IReadOnlyList<AssetFamilyListItem>> ListAsync(
        DbConnection conn, string? search, CancellationToken ct)
    {
        var like = "%" + (search ?? "").Trim() + "%";
        return (await conn.QueryAsync<AssetFamilyListItem>(new CommandDefinition(
            """
            select f.code as "Code",
                   f.name as "Name",
                   (select count(*)::int from asset_family_member m where m.family_code = f.code) as "MemberCount",
                   (select string_agg(m.asset_code, ' · ' order by m.asset_code)
                      from asset_family_member m where m.family_code = f.code) as "MembersSummary",
                   (select count(*)::int
                      from exchange_instrument i
                     where i.collect
                       and exists (select 1 from asset_family_member m
                                    where m.family_code = f.code
                                      and (m.asset_code = i.base_asset or m.asset_code = i.quote_asset))
                   ) as "InstrumentCount"
              from asset_family f
             where @search = '' or f.code ilike @like or coalesce(f.name, '') ilike @like
             order by f.code
            """,
            new { search = (search ?? "").Trim(), like },
            cancellationToken: ct))).ToList();
    }

    public static async Task<AssetFamilyDetails?> GetAsync(DbConnection conn, string code, CancellationToken ct)
    {
        var head = await conn.QuerySingleOrDefaultAsync<(string Code, string? Name, string? Note, DateTime CreatedAt, DateTime? UpdatedAt, string? UpdatedBy)>(
            new CommandDefinition(
                "select code, name, note, created_at, updated_at, updated_by from asset_family where code = @code",
                new { code },
                cancellationToken: ct));
        if (head.Code is null)
        {
            return null;
        }

        // Both counts, not one total: an asset can be a base on one venue and a quote on another,
        // and which of the two it is decides whether the fold changes a pair's heading or only its
        // rows. A single number would hide that.
        var members = (await conn.QueryAsync<AssetFamilyMemberRow>(new CommandDefinition(
            """
            select m.asset_code as "AssetCode",
                   m.note       as "Note",
                   m.created_by as "CreatedBy",
                   m.created_at as "CreatedAt",
                   (select count(*)::int from exchange_instrument i where i.base_asset  = m.asset_code) as "BaseListings",
                   (select count(*)::int from exchange_instrument i where i.quote_asset = m.asset_code) as "QuoteListings"
              from asset_family_member m
             where m.family_code = @code
             order by m.asset_code
            """,
            new { code },
            cancellationToken: ct))).ToList();

        return new AssetFamilyDetails(
            head.Code, head.Name, head.Note, head.CreatedAt, head.UpdatedAt, head.UpdatedBy, members);
    }

    /// <summary>Creates an empty family. Returns an error message, or null on success.</summary>
    public static async Task<string?> CreateAsync(
        DbConnection conn, string code, string? name, string? note, string? updatedBy, CancellationToken ct)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
        {
            return "Family code cannot be empty.";
        }

        // The code is what the pair heading prints, and it is joined against asset codes verbatim.
        // A space in it would render as a quote nobody can type.
        if (code.Any(char.IsWhiteSpace))
        {
            return "Family code cannot contain spaces.";
        }

        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from asset_family where code = @code)", new { code }, cancellationToken: ct));
        if (exists)
        {
            return $"Family '{code}' already exists.";
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into asset_family (code, name, note, updated_at, updated_by)
            values (@code, nullif(@name, ''), nullif(@note, ''), now(), @updatedBy)
            """,
            new { code, name, note, updatedBy },
            cancellationToken: ct));
        return null;
    }

    public static async Task<bool> UpdateAsync(
        DbConnection conn, string code, string? name, string? note, string? updatedBy, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            update asset_family
               set name = nullif(@name, ''), note = nullif(@note, ''),
                   updated_at = now(), updated_by = @updatedBy
             where code = @code
            """,
            new { code, name, note, updatedBy },
            cancellationToken: ct));
        return rows == 1;
    }

    /// <summary>
    /// Folds one asset into this family, moving it if it was folded elsewhere — the primary key on
    /// asset_code is what makes "an asset folds exactly one way" true, so an upsert is the only
    /// shape that cannot leave two answers behind.
    ///
    /// Returns an error message, or null on success.
    /// </summary>
    public static async Task<string?> AddMemberAsync(
        DbConnection conn, string familyCode, string assetCode, string? note, string? createdBy, CancellationToken ct)
    {
        assetCode = (assetCode ?? "").Trim();
        if (assetCode.Length == 0)
        {
            return "Asset code cannot be empty.";
        }

        var familyExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from asset_family where code = @familyCode)",
            new { familyCode }, cancellationToken: ct));
        if (!familyExists)
        {
            // The FK would raise this as a 500 otherwise, and the operator would learn nothing.
            return $"Unknown family '{familyCode}'.";
        }

        // Case matters: membership is joined against base_asset / quote_asset verbatim, so 'usdt'
        // would be a row that silently folds nothing. We do not upper-case it on the operator's
        // behalf — an asset code is the venue's spelling, not ours — but when the only difference
        // from a code we actually carry is case, say so with the real spelling in hand.
        var spelling = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            select c from (
                select base_asset  as c from exchange_instrument
                union select quote_asset from exchange_instrument
                union select code        from asset
            ) x
             where lower(c) = lower(@assetCode) and c <> @assetCode
             limit 1
            """,
            new { assetCode }, cancellationToken: ct));
        if (spelling is not null)
        {
            return $"No asset is spelled '{assetCode}'. It is spelled '{spelling}' — codes are matched exactly.";
        }

        // One level of folding only. Two hops would mean an asset folds two ways depending on where
        // the reader started, which is the one thing the primary key on asset_code exists to rule
        // out. The schema cannot express this (it needs a subquery, and this database has no
        // triggers by the convention set in 0001), so it is enforced here.
        //
        // The identity row — a family holding its own code, as USD holds USD — is not a chain and
        // is deliberately seeded, so it is exempt from both checks.
        if (!string.Equals(assetCode, familyCode, StringComparison.Ordinal))
        {
            var isFamily = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "select count(*) from asset_family_member where family_code = @assetCode and asset_code <> @assetCode",
                new { assetCode }, cancellationToken: ct));
            if (isFamily > 0)
            {
                return $"'{assetCode}' is itself a family with {isFamily} member(s). Move those members instead.";
            }

            var parent = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "select family_code from asset_family_member where asset_code = @familyCode and family_code <> @familyCode",
                new { familyCode }, cancellationToken: ct));
            if (parent is not null)
            {
                return $"'{familyCode}' itself folds into '{parent}'. Add the asset to '{parent}' instead.";
            }
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into asset_family_member (asset_code, family_code, note, created_by)
            values (@assetCode, @familyCode, nullif(@note, ''), @createdBy)
            on conflict (asset_code) do update set
                family_code = excluded.family_code,
                note        = excluded.note,
                created_by  = excluded.created_by,
                created_at  = now()
            """,
            new { assetCode, familyCode, note, createdBy },
            cancellationToken: ct));
        return null;
    }

    /// <summary>Unfolds one asset. Its family becomes itself again, which is what the absence of a
    /// row means everywhere it is read.</summary>
    public static async Task RemoveMemberAsync(DbConnection conn, string assetCode, CancellationToken ct) =>
        await conn.ExecuteAsync(new CommandDefinition(
            "delete from asset_family_member where asset_code = @assetCode",
            new { assetCode },
            cancellationToken: ct));

    /// <summary>Deletes a family only when nothing folds into it. Returns an error message, or null
    /// on success.</summary>
    public static async Task<string?> DeleteAsync(DbConnection conn, string code, CancellationToken ct)
    {
        var members = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from asset_family_member where family_code = @code", new { code }, cancellationToken: ct));
        if (members > 0)
        {
            return "Remove the members of this family first.";
        }

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "delete from asset_family where code = @code", new { code }, cancellationToken: ct));
        return rows == 1 ? null : "Family not found.";
    }
}

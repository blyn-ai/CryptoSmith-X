using System.Data.Common;
using System.Globalization;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// Canonical assets and their per-exchange aliases. The list and detail reads are derived from the
/// instruments and their latest snapshots; the writes (rename a canon, add/remove an alias, delete
/// an emptied canon) are the operator's, and the edit stamps who did it. Alias/asset writes return
/// an error string rather than throwing, so a bad code becomes an alert, not a 500.
/// </summary>
public static class AssetStore
{
    public static async Task<IReadOnlyList<AssetListItem>> ListAsync(DbConnection conn, string? search, CancellationToken ct)
    {
        var like = "%" + (search ?? "").Trim() + "%";
        return (await conn.QueryAsync<AssetListItem>(new CommandDefinition(
            """
            select a.code as "Code",
                   a.name as "Name",
                   (select count(*)::int from exchange_instrument i where i.base_asset = a.code) as "ListingCount",
                   (select string_agg(x.segment_code || ' ' || x.n, ' · ' order by x.segment_code)
                      from (select i.segment_code, count(*)::int as n
                              from exchange_instrument i
                             where i.base_asset = a.code
                             group by i.segment_code) x) as "ListingsSummary",
                   (select sum(l.open_interest * l.mark_price)
                      from exchange_instrument i
                      join market_snapshot_latest l on l.exchange_instrument_id = i.id
                     where i.base_asset = a.code) as "OpenInterestNotional",
                   (select extract(epoch from now() - min(l.received_at))::double precision
                      from exchange_instrument i
                      join market_snapshot_latest l on l.exchange_instrument_id = i.id
                     where i.base_asset = a.code) as "WorstSnapshotAgeSeconds"
              from asset a
             where @search = '' or a.code ilike @like or coalesce(a.name, '') ilike @like
             order by a.code
            """,
            new { search = (search ?? "").Trim(), like },
            cancellationToken: ct))).ToList();
    }

    public static async Task<AssetDetails?> GetAsync(DbConnection conn, string code, CancellationToken ct)
    {
        var head = await conn.QuerySingleOrDefaultAsync<(string Code, string? Name, string? Note, DateTime CreatedAt, DateTime? UpdatedAt, string? UpdatedBy)>(
            new CommandDefinition(
                "select code, name, note, created_at, updated_at, updated_by from asset where code = @code",
                new { code },
                cancellationToken: ct));
        if (head.Code is null)
        {
            return null;
        }

        var listings = (await conn.QueryAsync<AssetListing>(new CommandDefinition(
            """
            select i.id              as "InstrumentId",
                   i.segment_code   as "SegmentCode",
                   i.exchange_symbol as "Symbol",
                   i.status          as "Status",
                   i.collect         as "Collect",
                   l.last_price      as "LastPrice",
                   l.funding_rate    as "FundingRate",
                   l.open_interest * l.mark_price as "OpenInterestNotional",
                   case when (l.bid_price + l.ask_price) > 0
                        then (l.ask_price - l.bid_price) / ((l.bid_price + l.ask_price) / 2) * 10000
                   end               as "SpreadBps",
                   (l.depth_bid_25bps + l.depth_ask_25bps) as "Depth25Notional",
                   extract(epoch from now() - l.received_at)::double precision as "SnapshotAgeSeconds"
              from exchange_instrument i
              left join market_snapshot_latest l on l.exchange_instrument_id = i.id
             where i.base_asset = @code
             order by i.segment_code, i.exchange_symbol
            """,
            new { code },
            cancellationToken: ct))).ToList();

        var aliases = (await conn.QueryAsync<AssetAliasRow>(new CommandDefinition(
            """
            select segment_code as "SegmentCode",
                   alias         as "Alias",
                   asset_code    as "AssetCode",
                   multiplier    as "Multiplier",
                   note          as "Note"
              from asset_alias
             where asset_code = @code
             order by segment_code nulls first, alias
            """,
            new { code },
            cancellationToken: ct))).ToList();

        return new AssetDetails(
            head.Code, head.Name, head.Note, head.CreatedAt, head.UpdatedAt, head.UpdatedBy, listings, aliases);
    }

    public static async Task<bool> UpdateAsync(
        DbConnection conn, string code, string? name, string? note, string? updatedBy, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            update asset
               set name = nullif(@name, ''), note = nullif(@note, ''),
                   updated_at = now(), updated_by = @updatedBy
             where code = @code
            """,
            new { code, name, note, updatedBy },
            cancellationToken: ct));
        return rows == 1;
    }

    /// <summary>Adds or updates one alias. Returns an error message, or null on success.</summary>
    public static async Task<string?> AddAliasAsync(
        DbConnection conn, string assetCode, string? segmentCode, string alias, string multiplier, string? note, CancellationToken ct)
    {
        alias = (alias ?? "").Trim();
        if (alias.Length == 0)
        {
            return "Alias cannot be empty.";
        }

        if (!decimal.TryParse(multiplier, NumberStyles.Number, CultureInfo.InvariantCulture, out var mult) || mult <= 0)
        {
            return "Multiplier must be a positive number.";
        }

        segmentCode = string.IsNullOrWhiteSpace(segmentCode) ? null : segmentCode.Trim();
        if (segmentCode is not null)
        {
            var known = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from segment where code = @segmentCode)",
                new { segmentCode }, cancellationToken: ct));
            if (!known)
            {
                return $"Unknown exchange '{segmentCode}'. Leave blank for a global alias.";
            }
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into asset_alias (segment_code, alias, asset_code, multiplier, note)
            values (@segmentCode, @alias, @assetCode, @mult, nullif(@note, ''))
            on conflict (segment_code, alias) do update set
                asset_code = excluded.asset_code,
                multiplier = excluded.multiplier,
                note       = excluded.note
            """,
            new { segmentCode, alias, assetCode, mult, note },
            cancellationToken: ct));
        return null;
    }

    public static async Task DeleteAliasAsync(
        DbConnection conn, string? segmentCode, string alias, CancellationToken ct)
    {
        segmentCode = string.IsNullOrWhiteSpace(segmentCode) ? null : segmentCode.Trim();
        await conn.ExecuteAsync(new CommandDefinition(
            "delete from asset_alias where alias = @alias and segment_code is not distinct from @segmentCode",
            new { segmentCode, alias },
            cancellationToken: ct));
    }

    /// <summary>Deletes a canon only when nothing lists it and no alias points to it. Returns an
    /// error message, or null on success.</summary>
    public static async Task<string?> DeleteAssetAsync(DbConnection conn, string code, CancellationToken ct)
    {
        var listings = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from exchange_instrument where base_asset = @code", new { code }, cancellationToken: ct));
        if (listings > 0)
        {
            return "This asset still has listings; it cannot be deleted.";
        }

        var aliases = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from asset_alias where asset_code = @code", new { code }, cancellationToken: ct));
        if (aliases > 0)
        {
            return "Remove the aliases pointing to this asset first.";
        }

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "delete from asset where code = @code", new { code }, cancellationToken: ct));
        return rows == 1 ? null : "Asset not found.";
    }
}

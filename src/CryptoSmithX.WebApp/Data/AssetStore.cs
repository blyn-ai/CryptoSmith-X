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
                   (select string_agg(x.exchange_code || ' ' || x.n, ' · ' order by x.exchange_code)
                      from (select i.exchange_code, count(*)::int as n
                              from exchange_instrument i
                             where i.base_asset = a.code
                             group by i.exchange_code) x) as "ListingsSummary",
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
                   i.exchange_code   as "ExchangeCode",
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
             order by i.exchange_code, i.exchange_symbol
            """,
            new { code },
            cancellationToken: ct))).ToList();

        var aliases = (await conn.QueryAsync<AssetAliasRow>(new CommandDefinition(
            """
            select exchange_code as "ExchangeCode",
                   alias         as "Alias",
                   asset_code    as "AssetCode",
                   multiplier    as "Multiplier",
                   note          as "Note"
              from asset_alias
             where asset_code = @code
             order by exchange_code nulls first, alias
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
        DbConnection conn, string assetCode, string? exchangeCode, string alias, string multiplier, string? note, CancellationToken ct)
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

        exchangeCode = string.IsNullOrWhiteSpace(exchangeCode) ? null : exchangeCode.Trim();
        if (exchangeCode is not null)
        {
            var known = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from exchange where code = @exchangeCode)",
                new { exchangeCode }, cancellationToken: ct));
            if (!known)
            {
                return $"Unknown exchange '{exchangeCode}'. Leave blank for a global alias.";
            }
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into asset_alias (exchange_code, alias, asset_code, multiplier, note)
            values (@exchangeCode, @alias, @assetCode, @mult, nullif(@note, ''))
            on conflict (exchange_code, alias) do update set
                asset_code = excluded.asset_code,
                multiplier = excluded.multiplier,
                note       = excluded.note
            """,
            new { exchangeCode, alias, assetCode, mult, note },
            cancellationToken: ct));
        return null;
    }

    public static async Task DeleteAliasAsync(
        DbConnection conn, string? exchangeCode, string alias, CancellationToken ct)
    {
        exchangeCode = string.IsNullOrWhiteSpace(exchangeCode) ? null : exchangeCode.Trim();
        await conn.ExecuteAsync(new CommandDefinition(
            "delete from asset_alias where alias = @alias and exchange_code is not distinct from @exchangeCode",
            new { exchangeCode, alias },
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

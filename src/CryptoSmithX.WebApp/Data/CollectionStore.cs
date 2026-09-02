using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>The Collections catalogue (screen 3): defaults per collection, and which enabled
/// exchanges depart from them.</summary>
public static class CollectionStore
{
    public static async Task<IReadOnlyList<CollectionCard>> ListAsync(DbConnection conn, CancellationToken ct)
    {
        var collections = (await conn.QueryAsync<(string Code, string Name, string Description, string Kind, short SortOrder, string DefaultMode, int? DefaultIntervalS, int? DefaultRetentionDays)>(
            new CommandDefinition(
                """
                select code as "Code", name as "Name", description as "Description", kind as "Kind",
                       sort_order as "SortOrder", default_mode as "DefaultMode",
                       default_interval_s as "DefaultIntervalS", default_retention_days as "DefaultRetentionDays"
                  from collection order by sort_order
                """,
                cancellationToken: ct))).ToList();

        var rows = (await conn.QueryAsync<CollectionVenueRow>(new CommandDefinition(
            """
            select ec.collection_code as "CollectionCode",
                   ec.exchange_code   as "ExchangeCode",
                   e.name             as "ExchangeName",
                   ec.mode            as "Mode",
                   ec.note            as "Note"
              from exchange_collection ec
              join exchange e on e.code = ec.exchange_code
             where e.status = 'enabled'
             order by e.code
            """,
            cancellationToken: ct))).ToList();

        return collections
            .Select(c => new CollectionCard(
                c.Code, c.Name, c.Description, c.Kind, c.SortOrder,
                c.DefaultMode, c.DefaultIntervalS, c.DefaultRetentionDays,
                rows.Where(r => r.CollectionCode == c.Code).ToList()))
            .ToList();
    }
}

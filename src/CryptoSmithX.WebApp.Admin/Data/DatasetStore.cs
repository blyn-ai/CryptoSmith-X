using System.Data.Common;
using CryptoSmithX.WebApp.Admin.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Admin.Data;

/// <summary>The Datasets catalogue (screen 3): defaults per dataset, and which enabled
/// exchanges depart from them.</summary>
public static class DatasetStore
{
    public static async Task<IReadOnlyList<DatasetCard>> ListAsync(DbConnection conn, CancellationToken ct)
    {
        var datasets = (await conn.QueryAsync<(string Code, string Name, string Description, string Kind, short SortOrder, string DefaultMode, int? DefaultIntervalS, int? DefaultRetentionDays)>(
            new CommandDefinition(
                """
                select code as "Code", name as "Name", description as "Description", kind as "Kind",
                       sort_order as "SortOrder", default_mode as "DefaultMode",
                       default_interval_s as "DefaultIntervalS", default_retention_days as "DefaultRetentionDays"
                  from dataset order by sort_order
                """,
                cancellationToken: ct))).ToList();

        var rows = (await conn.QueryAsync<DatasetVenueRow>(new CommandDefinition(
            """
            select ec.dataset_code as "DatasetCode",
                   ec.segment_code   as "SegmentCode",
                   e.name             as "ExchangeName",
                   ec.mode            as "Mode",
                   ec.note            as "Note"
              from segment_dataset ec
              join segment e on e.code = ec.segment_code
             where e.status = 'enabled'
             order by e.code
            """,
            cancellationToken: ct))).ToList();

        return datasets
            .Select(c => new DatasetCard(
                c.Code, c.Name, c.Description, c.Kind, c.SortOrder,
                c.DefaultMode, c.DefaultIntervalS, c.DefaultRetentionDays,
                rows.Where(r => r.DatasetCode == c.Code).ToList()))
            .ToList();
    }
}

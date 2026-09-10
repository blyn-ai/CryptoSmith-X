using System.Data.Common;
using System.Text.Json;
using CryptoSmithX.WebApp.Studio.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Studio.Data;

/// <summary>
/// What band 1's listing-cell disclosure needs (B2): the instrument as <c>exchange_instrument</c>
/// holds it right now, plus the open version of its typed spec history. <c>instrument_spec</c> was
/// already granted to <c>studio_reader</c> by 0031 — this is the first code in WebApp.Studio to read
/// it.
/// </summary>
public static class InstrumentDetailStore
{
    /// <summary>
    /// LEFT JOIN, not INNER: <c>instrument_spec_open</c> guarantees at most one open version per
    /// instrument, never at least one — discovery has to have run since 0031 for a version to exist
    /// at all, and an instrument with none is a real state (see <see cref="InstrumentDetail.Spec"/>),
    /// not a query bug.
    /// </summary>
    public const string Sql =
        """
        select i.base_asset_raw                     as "BaseAssetRaw",
               i.quote_asset_raw                     as "QuoteAssetRaw",
               i.contract_multiplier::double precision as "ContractMultiplier",
               i.listed_at                           as "ListedAt",
               i.first_seen_at                       as "FirstSeenAt",
               i.price_step::double precision        as "PriceStep",
               i.qty_step::double precision          as "QtyStep",
               i.min_qty::double precision           as "MinQty",
               i.min_notional::double precision      as "MinNotional",
               i.status                              as "Status",
               i.status_changed_at                   as "StatusChangedAt",
               i.collect                             as "Collect",
               i.collect_note                        as "CollectNote",
               i.collect_changed_at                  as "ChangedAt",
               i.collect_changed_by                  as "ChangedBy",
               s.valid_from                          as "ValidFrom",
               s.valid_to                             as "ValidTo",
               s.last_seen_at                         as "SpecLastSeenAt",
               s.venue_effective_at                   as "VenueEffectiveAt",
               s.spec_hash                            as "SpecHash",
               s.written_by                           as "WrittenBy",
               s.funding_interval_source              as "FundingIntervalSource",
               s.raw_json::text                       as "RawJson"
          from exchange_instrument i
          left join instrument_spec s
            on s.exchange_instrument_id = i.id and s.valid_to is null
         where i.id = @instrumentId
        """;

    public static async Task<InstrumentDetail?> DetailAsync(
        DbConnection conn, int instrumentId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(
                string BaseAssetRaw, string QuoteAssetRaw, double ContractMultiplier,
                DateTime? ListedAt, DateTime FirstSeenAt,
                double PriceStep, double QtyStep, double MinQty, double? MinNotional,
                string Status, DateTime StatusChangedAt, bool Collect, string? CollectNote,
                DateTime? ChangedAt, string? ChangedBy,
                DateTime? ValidFrom, DateTime? ValidTo, DateTime? SpecLastSeenAt,
                DateTime? VenueEffectiveAt, string? SpecHash, string? WrittenBy,
                string? FundingIntervalSource, string? RawJson)>(
            new CommandDefinition(Sql, new { instrumentId }, cancellationToken: ct));

        // QuerySingleOrDefaultAsync on a value tuple never hands back a real "no row" — every field
        // just arrives at its default. BaseAssetRaw is not-null on exchange_instrument, so an empty
        // one is the tell that the WHERE found nothing.
        if (string.IsNullOrEmpty(row.BaseAssetRaw))
        {
            return null;
        }

        var spec = row.ValidFrom is { } validFrom
            ? new InstrumentSpecVersion(
                validFrom, row.ValidTo, row.SpecLastSeenAt!.Value, row.VenueEffectiveAt,
                row.SpecHash!, row.WrittenBy!, row.FundingIntervalSource, Pretty(row.RawJson))
            : null;

        return new InstrumentDetail(
            new InstrumentIdentity(
                row.BaseAssetRaw, row.QuoteAssetRaw, row.ContractMultiplier, row.ListedAt, row.FirstSeenAt),
            new InstrumentLimits(row.PriceStep, row.QtyStep, row.MinQty, row.MinNotional),
            new InstrumentStatus(
                row.Status, row.StatusChangedAt, row.Collect, row.CollectNote, row.ChangedAt, row.ChangedBy),
            spec);
    }

    /// <summary>Indented for the disclosure's <c>&lt;pre&gt;</c> block. Falls back to the compact
    /// text as stored if it somehow does not parse as JSON — a malformed document is not this view's
    /// business to hide.</summary>
    private static string Pretty(string? rawJson)
    {
        if (string.IsNullOrEmpty(rawJson))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }
}

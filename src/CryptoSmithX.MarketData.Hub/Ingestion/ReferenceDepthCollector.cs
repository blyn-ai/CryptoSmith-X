using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Connectors;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Writes <c>vault_reference_depth</c> — an external venue's own book, as watched by a vault-backed
/// venue's risk engine. See <see cref="Market.ReferenceDepth"/> and the 0052 migration header for
/// why this is a dataset of its own rather than folded into <see cref="DepthCollector"/>: it is
/// never this venue's own resting liquidity, and a reader must not be able to mistake the two.
/// </summary>
public sealed class ReferenceDepthCollector
{
    /// <summary>Same predicate as every other collector: an instrument an operator switched off
    /// stops being written here too.</summary>
    internal const string TargetInstrumentsSql =
        "select exchange_symbol, id from exchange_instrument "
        + "where segment_code = @code and collect = true and status = 'trading'";

    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;

    public ReferenceDepthCollector(IExchangeMarketData adapter, Db db)
    {
        _adapter = adapter;
        _db = db;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var rows = await _adapter.GetReferenceDepthAsync(ct);
        if (rows.Count == 0)
        {
            return 0;
        }

        await using var conn = await _db.OpenAsync(ct);
        var ids = (await conn.QueryAsync<(string Symbol, int Id)>(new CommandDefinition(
                TargetInstrumentsSql, new { code = _adapter.SegmentCode }, cancellationToken: ct)))
            .ToDictionary(r => r.Symbol, r => r.Id, StringComparer.Ordinal);

        var payload = new List<Row>(rows.Count);
        foreach (var r in rows)
        {
            if (!ids.TryGetValue(r.ExchangeSymbol, out var id))
            {
                // Seen by the risk engine but not yet by discovery, or switched off by an operator
                // — the same reason VaultCollector skips a row rather than failing the whole pass.
                continue;
            }

            payload.Add(new Row(id, r.Source, r.At, r.CumulativeBidQty, r.CumulativeAskQty, r.VenueAgeSeconds));
        }

        if (payload.Count == 0)
        {
            return 0;
        }

        return await BulkJson.WriteAsync(
            conn, null,
            """
            insert into vault_reference_depth (
                exchange_instrument_id, source, received_at,
                cumulative_bid_qty, cumulative_ask_qty, venue_age_seconds)
            select exchange_instrument_id, source, received_at,
                   cumulative_bid_qty, cumulative_ask_qty, venue_age_seconds
              from jsonb_to_recordset(@rows) as x(
                   exchange_instrument_id integer, source text, received_at timestamptz,
                   cumulative_bid_qty double precision, cumulative_ask_qty double precision,
                   venue_age_seconds double precision)
            on conflict (exchange_instrument_id, source, received_at) do update set
                cumulative_bid_qty = excluded.cumulative_bid_qty,
                cumulative_ask_qty = excluded.cumulative_ask_qty,
                venue_age_seconds  = excluded.venue_age_seconds
            """,
            payload, ct);
    }

    private sealed record Row(
        int exchange_instrument_id,
        string source,
        DateTimeOffset received_at,
        double? cumulative_bid_qty,
        double? cumulative_ask_qty,
        double? venue_age_seconds);
}

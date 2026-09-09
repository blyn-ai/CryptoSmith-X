using System.Text.Json;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Writes the trade tape. Unlike every other collector here this one does not ask a venue for
/// anything: the socket has already been receiving trades and buffering them
/// (<see cref="Connectors.Streaming.EventBuffer{T}"/>), and a pass simply takes everything buffered
/// since the last one and writes it.
///
/// That inversion is the whole difference between a poll and a push dataset. A ticker has a current
/// value that can be asked for at any moment; a trade does not — the second trade does not replace
/// the first, and a trade nobody drained is a trade that never happened as far as the database is
/// concerned. So the loop's interval here is a FLUSH cadence, not a sampling rate: it decides how
/// much is held in memory between writes, never how much of the market is seen.
/// </summary>
public sealed class TradeCollector
{
    // Trades are stored for instruments an operator turned collection on for, same predicate the
    // candle collector uses. A trade for anything else is dropped rather than stored under an id we
    // would have to invent — the socket's subscription list and this list are refreshed
    // independently, so they disagree for a few minutes after a listing changes.
    internal const string TargetInstrumentsSql =
        """
        select exchange_symbol, id
          from exchange_instrument
         where segment_code = @code and collect = true
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public TradeCollector(IExchangeMarketData adapter, Db db, TimeProvider clock, ILogger logger)
    {
        _adapter = adapter;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Returns the number of trades written.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var drained = _adapter.DrainTrades();
        if (drained.Count == 0)
        {
            return 0;
        }

        await using var conn = await _db.OpenAsync(ct);
        var ids = (await conn.QueryAsync<(string Symbol, int Id)>(new CommandDefinition(
                TargetInstrumentsSql, new { code = _adapter.SegmentCode }, cancellationToken: ct)))
            .ToDictionary(r => r.Symbol, r => r.Id, StringComparer.Ordinal);

        await Partitions.EnsureAsync(conn, _clock.GetUtcNow(), ct);

        var receivedAt = _clock.GetUtcNow();
        var rows = new List<TradeRow>(drained.Count);
        var unknown = 0;
        foreach (var t in drained)
        {
            if (!ids.TryGetValue(t.ExchangeSymbol, out var id))
            {
                unknown++;
                continue;
            }

            rows.Add(new TradeRow(
                id, t.EventTime, t.VenueUid, t.Seq, receivedAt, "ws",
                t.Price, t.Qty, t.TakerSide, t.TradeType));
        }

        if (unknown > 0)
        {
            // Expected in small numbers right after a listing change, and a bug in any other
            // circumstance — the socket subscribes from the venue's list, this reads ours.
            _logger.LogDebug(
                "{Exchange}/trades dropped {Unknown} trades for symbols not in the collected set",
                _adapter.SegmentCode, unknown);
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        return await WriteAsync(conn, rows, ct);
    }

    /// <summary>
    /// One statement per pass, through jsonb rather than unnest: a trade row is flat, but this
    /// collector shares its writing shape with <see cref="BookCollector"/>, whose rows carry
    /// per-row arrays that unnest cannot express at all. Keeping both on the same idiom is worth
    /// more than saving the serialisation here.
    ///
    /// ON CONFLICT DO NOTHING, not update: a trade is immutable once the venue has published it,
    /// and the conflict case is real and routine — every reconnect replays a snapshot backlog
    /// (Kraken) and a drain can overlap a previous one's tail.
    /// </summary>
    private static async Task<int> WriteAsync(NpgsqlConnection conn, IReadOnlyList<TradeRow> rows, CancellationToken ct)
    {
        return await BulkJson.WriteAsync(
            conn, null,
            """
            insert into trade (
                exchange_instrument_id, event_time, venue_uid, seq, received_at, source,
                price, qty, taker_side, trade_type)
            select exchange_instrument_id, event_time, venue_uid, seq, received_at, source,
                   price, qty, taker_side, trade_type
              from jsonb_to_recordset(@rows) as x(
                   exchange_instrument_id integer, event_time timestamptz, venue_uid text,
                   seq bigint, received_at timestamptz, source text,
                   price numeric, qty numeric, taker_side text, trade_type text)
            on conflict (exchange_instrument_id, event_time, venue_uid) do nothing
            """,
            rows, ct);
    }

    /// <summary>Property names match the column names the statement above declares — the mapping is
    /// the JSON, so a rename on either side has to be made on both.</summary>
    private sealed record TradeRow(
        int exchange_instrument_id,
        DateTimeOffset event_time,
        string venue_uid,
        long? seq,
        DateTimeOffset received_at,
        string source,
        double price,
        double qty,
        string taker_side,
        string? trade_type);
}

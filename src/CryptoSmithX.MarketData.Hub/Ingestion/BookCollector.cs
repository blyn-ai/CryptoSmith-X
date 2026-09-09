using System.Text.Json;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Writes <c>book_topn</c>: the top N levels of every collected instrument's book, as the socket
/// currently holds it.
///
/// A POLL over a pushed book, deliberately. Every maintained book is already up to date in memory —
/// storing every frame would mean a row per depth message per symbol, which on Binance alone is
/// hundreds a second. The frames are therefore SAMPLED at the loop's interval, and each row says so
/// honestly: <c>observed_at</c> and <c>seq</c> are the venue's own for the last frame applied, so a
/// reader can see exactly which frame was stored and that the ones between were not.
///
/// This is also why <c>is_snapshot</c> is always true here: what is stored is a whole top-of-book,
/// never a diff.
/// </summary>
public sealed class BookCollector
{
    internal const string TargetInstrumentsSql =
        """
        select exchange_symbol, id
          from exchange_instrument
         where segment_code = @code and collect = true and status = 'trading'
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;
    private readonly DbSettings _settings;
    private readonly TimeProvider _clock;

    public BookCollector(IExchangeMarketData adapter, Db db, DbSettings settings, TimeProvider clock)
    {
        _adapter = adapter;
        _db = db;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>Returns the number of frames written.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // How deep to store, from the dataset's own setting (0034 seeded book.levels = 25) rather
        // than a constant here: it is the kind of number an operator changes without a deploy.
        var levels = (await _settings.CurrentAsync(ct)).DatasetSettingInt("book", "levels");
        if (levels <= 0)
        {
            return 0;
        }

        await using var conn = await _db.OpenAsync(ct);
        var targets = (await conn.QueryAsync<(string Symbol, int Id)>(new CommandDefinition(
            TargetInstrumentsSql, new { code = _adapter.SegmentCode }, cancellationToken: ct))).ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        await Partitions.EnsureAsync(conn, _clock.GetUtcNow(), ct);

        var receivedAt = _clock.GetUtcNow();
        var rows = new List<BookRow>(targets.Count);
        foreach (var (symbol, id) in targets)
        {
            // A symbol whose book is unseeded, dirty or simply not subscribed yet is skipped, not
            // written empty: book_topn's own CHECK requires both sides to be present, and a row
            // claiming an empty book would be a measurement nobody made.
            if (!_adapter.TryGetBookFrame(symbol, levels, out var frame))
            {
                continue;
            }

            rows.Add(new BookRow(
                id, frame.ObservedAt, frame.Seq, receivedAt, frame.IsSnapshot, (short)frame.Levels,
                frame.BidPrices, frame.BidQuantities, frame.AskPrices, frame.AskQuantities));
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        await using var cmd = new NpgsqlCommand(
            """
            insert into book_topn (
                exchange_instrument_id, observed_at, seq, received_at, is_snapshot, levels,
                bid_px, bid_qty, ask_px, ask_qty)
            select exchange_instrument_id, observed_at, seq, received_at, is_snapshot, levels,
                   bid_px, bid_qty, ask_px, ask_qty
              from jsonb_to_recordset(@rows) as x(
                   exchange_instrument_id integer, observed_at timestamptz, seq bigint,
                   received_at timestamptz, is_snapshot boolean, levels smallint,
                   bid_px numeric[], bid_qty numeric[], ask_px numeric[], ask_qty numeric[])
            -- Sampling the same unchanged book twice is the normal case on a quiet symbol: the
            -- venue's own (observed_at, seq) then repeat, and the second sample is not a second
            -- observation. DO NOTHING rather than a second row saying the same thing.
            on conflict (exchange_instrument_id, observed_at, seq) do nothing
            """,
            conn);

        // jsonb rather than unnest, and here it is not a preference: each row carries FOUR
        // variable-length arrays, and Postgres has no jagged array type for unnest to walk.
        var json = cmd.Parameters.Add("rows", NpgsqlTypes.NpgsqlDbType.Jsonb);
        json.Value = JsonSerializer.Serialize(rows);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record BookRow(
        int exchange_instrument_id,
        DateTimeOffset observed_at,
        long seq,
        DateTimeOffset received_at,
        bool is_snapshot,
        short levels,
        IReadOnlyList<double> bid_px,
        IReadOnlyList<double> bid_qty,
        IReadOnlyList<double> ask_px,
        IReadOnlyList<double> ask_qty);
}

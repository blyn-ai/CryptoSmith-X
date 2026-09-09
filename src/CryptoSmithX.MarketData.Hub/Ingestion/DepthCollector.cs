using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Fills the order-book depth on <c>market_snapshot_latest</c>. Unlike the rest of the snapshot the
/// book is a per-symbol call, so it runs as its own slower loop (0001 always reserved a 'depth'
/// collector) and only for instruments the venue lists as <c>trading</c>. A symbol not reached
/// within a pass keeps its previous depth and its honestly older <c>depth_at</c> — the snapshot
/// writer leaves depth untouched whenever a ticker carries no book, so the two never fight.
/// Adapters that carry the book inline in the ticker (the fake) return null here and this no-ops.
///
/// The pass is walked by a small pool of workers rather than one at a time. Serial walking paid the
/// venue's round trip once per symbol, and that round trip and the pause were serialised end to
/// end: on production WEEX, 1005 instruments at a 50 ms pause plus a ~309 ms round trip took 361 s
/// against a 60 s interval with the host idle at load 0.34 — a latency bill, not a rate limit. The
/// actual sustained rate that pass delivered was 1005 / 361 s ≈ 2.78 req/s, an order of magnitude
/// under the ~20 req/s the pause was meant to hold the venue to. Both ceilings now come from the
/// venue's <see cref="VenueGate"/> (0021), shared with every other caller on the same IP:
/// concurrency removes the latency bill, and the gate's own pacing is what now actually sustains
/// the venue's configured req/s, rather than merely intending to and falling an order of magnitude
/// short.
/// </summary>
public sealed class DepthCollector
{
    // Only trading instruments have a book worth measuring, and only ones an operator left collect on.
    internal const string TargetInstrumentsSql =
        """
        select id, exchange_symbol
          from exchange_instrument
         where segment_code = @code and collect = true and status = 'trading'
         order by exchange_symbol
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;
    private readonly VenueGate _gate;

    // No TimeProvider any more: the pass no longer paces itself. Pacing is the venue's, held by the
    // gate, which owns the clock — a second clock here would be a second opinion about the same
    // schedule.
    public DepthCollector(IExchangeMarketData adapter, Db db, VenueGate gate)
    {
        _adapter = adapter;
        _db = db;
        _gate = gate;
    }

    /// <summary>
    /// Returns the number of instruments whose depth was refreshed this pass.
    ///
    /// A symbol that throws still fails the whole pass, exactly as when this walked serially: the
    /// exception is re-thrown once the other workers have stopped, the loop records ok=false and
    /// <c>ExchangeWorker.RecordGapAsync</c> opens a <c>collector_gap</c>. That contract is the point
    /// — a pass with a hole in it reported as a success is a lie about what we observed — so the
    /// catch inside <see cref="Sweep.RunAsync{T}"/> records and re-throws; it never swallows.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        var targets = (await conn.QueryAsync<(int Id, string Symbol)>(new CommandDefinition(
            TargetInstrumentsSql,
            new { code = _adapter.SegmentCode },
            cancellationToken: ct))).ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        // Fetched books are gathered here and written ONCE after the sweep, not one UPDATE per
        // symbol behind a single-slot write lane. Measured on test, schema 0038, warmed: 275 Kraken
        // instruments at one UPDATE each took 2.67 s serialised, while the HTTP side of the same
        // pass — 32 wide — finished in a fraction of a second. Same fix as SnapshotCollector
        // (e590bc1): a ConcurrentBag rather than a lock, since workers only ever append and never
        // read each other's entries.
        var fetched = new System.Collections.Concurrent.ConcurrentBag<(int Id, Depth Depth)>();

        var result = await Sweep.RunAsync(
            targets,
            _gate.MaxConcurrentRequests,
            async (target, workCt) =>
            {
                var (id, symbol) = target;

                // The lease covers the adapter call whether or not it reaches the network: an
                // adapter serving the book from a live WS cache looks identical from here. That
                // over-counts the budget on the WS venues and never under-counts it, which is the
                // safe direction for a ceiling.
                Depth? depth;
                using (await _gate.AcquireAsync(workCt).ConfigureAwait(false))
                {
                    depth = await _adapter.GetOrderBookAsync(symbol, workCt).ConfigureAwait(false);
                }

                if (depth is null)
                {
                    return 0;
                }

                fetched.Add((id, depth));
                return 1;
            },
            ex =>
            {
                // A venue that pushed us away holds back every caller on this IP, not just this
                // collector: that is what a venue-wide gate is for.
                VenuePenalty.Apply(_gate, ex);
            },
            // A pass with a hole in it must arrive as a thrown exception, never as a smaller
            // "successful" count: CollectorLoop would report ok=true and RecordGapAsync would never
            // open a collector_gap for an outage nobody saw.
            SweepFailure.FailFast,
            ct).ConfigureAwait(false);

        if (fetched.IsEmpty)
        {
            return 0;
        }

        // The real count, not result.Written (which only says how many books were fetched): a plain
        // UPDATE against an instrument with no market_snapshot_latest row yet still affects zero
        // rows, harmlessly, until the first snapshot for it lands — same contract as before, now
        // reported from the one statement that actually touches the table instead of per symbol.
        return await WriteDepthBatchAsync(conn, fetched, ct);
    }

    /// <summary>Update only the depth columns for every instrument fetched this pass, in one
    /// statement. The row itself is owned by the snapshot writer; an instrument unnest carries but
    /// that has no row yet is matched by nothing on the update side and simply does not affect one —
    /// same as the per-row UPDATE it replaces.</summary>
    private static async Task<int> WriteDepthBatchAsync(
        NpgsqlConnection conn, IReadOnlyCollection<(int Id, Depth Depth)> fetched, CancellationToken ct)
    {
        var ids = new int[fetched.Count];
        var bid10 = new double?[fetched.Count];
        var ask10 = new double?[fetched.Count];
        var bid25 = new double?[fetched.Count];
        var ask25 = new double?[fetched.Count];
        var bid50 = new double?[fetched.Count];
        var ask50 = new double?[fetched.Count];
        var at = new DateTimeOffset[fetched.Count];
        // depth_ref/book_reach: on WEEX, Binance and Hyperliquid this pass is the ONLY source —
        // their ticker never carries a Depth (SnapshotCollector's own upsert always writes NULL for
        // these two there, deferring to whatever this statement last wrote). Phase 4 item 2
        // (plans/prompt-collect-everything.md) added these to Ticker/SnapshotCollector for the
        // venues whose ticker DOES carry depth (Kraken WS) but missed this collector, which is the
        // only writer for the other three — found live on test, not from re-reading the diff.
        var depthRef = new double?[fetched.Count];
        var reachBid = new double?[fetched.Count];
        var reachAsk = new double?[fetched.Count];

        var i = 0;
        foreach (var (id, depth) in fetched)
        {
            ids[i] = id;
            bid10[i] = depth.Bid10Bps;
            ask10[i] = depth.Ask10Bps;
            bid25[i] = depth.Bid25Bps;
            ask25[i] = depth.Ask25Bps;
            bid50[i] = depth.Bid50Bps;
            ask50[i] = depth.Ask50Bps;
            at[i] = depth.At;
            depthRef[i] = depth.Mid;
            reachBid[i] = depth.ReachBidBps;
            reachAsk[i] = depth.ReachAskBps;
            i++;
        }

        // NpgsqlCommand rather than Dapper: Dapper expands any IEnumerable parameter into
        // @p1, @p2, …, which turns unnest(@ids) into unnest((@ids1,@ids2,…)) and fails.
        await using var cmd = new NpgsqlCommand(
            """
            update market_snapshot_latest as m set
                depth_bid_10bps = v.bid10, depth_ask_10bps = v.ask10,
                depth_bid_25bps = v.bid25, depth_ask_25bps = v.ask25,
                depth_bid_50bps = v.bid50, depth_ask_50bps = v.ask50,
                depth_at        = v.at,
                depth_ref       = v.depth_ref,
                book_reach_bid  = v.reach_bid,
                book_reach_ask  = v.reach_ask
            from unnest(@ids, @bid10, @ask10, @bid25, @ask25, @bid50, @ask50, @at, @depth_ref, @reach_bid, @reach_ask)
                as v(id, bid10, ask10, bid25, ask25, bid50, ask50, at, depth_ref, reach_bid, reach_ask)
            where m.exchange_instrument_id = v.id
            """,
            conn);

        cmd.Parameters.AddWithValue("ids", ids);
        cmd.Parameters.AddWithValue("bid10", bid10);
        cmd.Parameters.AddWithValue("ask10", ask10);
        cmd.Parameters.AddWithValue("bid25", bid25);
        cmd.Parameters.AddWithValue("ask25", ask25);
        cmd.Parameters.AddWithValue("bid50", bid50);
        cmd.Parameters.AddWithValue("ask50", ask50);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("depth_ref", depthRef);
        cmd.Parameters.AddWithValue("reach_bid", reachBid);
        cmd.Parameters.AddWithValue("reach_ask", reachAsk);

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

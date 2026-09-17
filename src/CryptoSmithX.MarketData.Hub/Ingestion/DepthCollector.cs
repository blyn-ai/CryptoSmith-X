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
    /// A pass with a hole in it still arrives as a thrown exception — the loop records ok=false and
    /// <c>ExchangeWorker.RecordGapAsync</c> opens a <c>collector_gap</c>; a hole reported as success
    /// would be a lie about what we observed. What changed (2026-09-17) is what happens BEFORE the
    /// throw. The pass used to stop at the first failure and discard every book already fetched, so
    /// one refusal out of 35 threw away 34 good books: on production OKX roughly one pass in five
    /// did exactly that, and after two in a row the whole venue's depth was twenty minutes stale.
    /// Now every symbol is tried, a symbol the venue refused as too fast is tried once more after
    /// the gate's penalty, everything fetched is written, and only then is the hole reported.
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

        var outcome = await FetchAsync(
            targets,
            _gate,
            (target, workCt) => _adapter.GetOrderBookAsync(target.Symbol, workCt),
            ct).ConfigureAwait(false);

        // Written ONCE after the sweep, not one UPDATE per symbol behind a single-slot write lane.
        // Measured on test, schema 0038, warmed: 275 Kraken instruments at one UPDATE each took
        // 2.67 s serialised, while the HTTP side of the same pass finished in a fraction of a second.
        var written = outcome.Fetched.Count == 0
            ? 0
            : await WriteDepthBatchAsync(conn, outcome.Fetched.Select(f => (f.Item.Id, f.Book)).ToList(), ct);

        if (outcome.Missing.Count > 0)
        {
            var (first, error) = outcome.Missing[0];
            throw new DepthPassIncompleteException(
                $"{outcome.Missing.Count} of {targets.Count} books not fetched ({outcome.Fetched.Count} fetched and written); "
                + $"first missing {first.Symbol}: {error.Message}",
                error);
        }

        return written;
    }

    /// <summary>What a depth sweep produced: the books it has, and the items it could not get with
    /// the error that stopped each.</summary>
    internal sealed record FetchOutcome<T, TBook>(
        IReadOnlyList<(T Item, TBook Book)> Fetched,
        IReadOnlyList<(T Item, Exception Error)> Missing);

    /// <summary>
    /// Every item tried under the venue gate; items refused as too fast tried once more (the gate
    /// has been penalised by then, so the second round waits the venue's cooldown); nothing
    /// fetched is dropped. Generic and database-free so the contract has a unit test.
    /// </summary>
    internal static async Task<FetchOutcome<T, TBook>> FetchAsync<T, TBook>(
        IReadOnlyList<T> items,
        VenueGate gate,
        Func<T, CancellationToken, Task<TBook?>> fetch,
        CancellationToken ct)
        where TBook : class
    {
        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<int, (T Item, TBook Book)>();
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<int, (T Item, Exception Error)>();

        async Task PassAsync(IReadOnlyList<(int Index, T Item)> list)
        {
            await Sweep.RunAsync(
                list,
                gate.MaxConcurrentRequests,
                async (entry, workCt) =>
                {
                    TBook? book;
                    try
                    {
                        // The lease covers the adapter call whether or not it reaches the network:
                        // an adapter serving the book from a live WS cache looks identical from here.
                        // That over-counts the budget on the WS venues and never under-counts it.
                        using (await gate.AcquireAsync(workCt).ConfigureAwait(false))
                        {
                            book = await fetch(entry.Item, workCt).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        errors[entry.Index] = (entry.Item, ex);
                        throw;
                    }

                    errors.TryRemove(entry.Index, out _);
                    if (book is null)
                    {
                        return 0;
                    }

                    fetched[entry.Index] = (entry.Item, book);
                    return 1;
                },
                // A venue that pushed us away holds back every caller on this IP, not just this
                // collector: that is what a venue-wide gate is for.
                ex => VenuePenalty.Apply(gate, ex),
                SweepFailure.Isolate,
                ct).ConfigureAwait(false);
        }

        await PassAsync(items.Select((item, i) => (i, item)).ToList()).ConfigureAwait(false);

        var refused = errors
            .Where(e => e.Value.Error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
            .OrderBy(e => e.Key)
            .Select(e => (e.Key, e.Value.Item))
            .ToList();
        if (refused.Count > 0)
        {
            await PassAsync(refused).ConfigureAwait(false);
        }

        return new FetchOutcome<T, TBook>(
            fetched.OrderBy(f => f.Key).Select(f => f.Value).ToList(),
            errors.OrderBy(e => e.Key).Select(e => e.Value).ToList());
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

/// <summary>A depth pass that fetched some books but not all. The books it did fetch are already
/// written when this is thrown; the throw is what makes the loop record the hole.</summary>
public sealed class DepthPassIncompleteException(string message, Exception inner) : Exception(message, inner);

using System.Net;
using System.Runtime.ExceptionServices;
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
    /// catch inside <see cref="SweepAsync{T}"/> records and re-throws; it never swallows.
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

        // One connection for the pass, as before: the writes are milliseconds against a network call
        // of hundreds, so a single write lane behind the fetches costs nothing and keeps this loop's
        // footprint on the pool at exactly one connection.
        using var writeLane = new SemaphoreSlim(1, 1);

        return await SweepAsync(
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

                await writeLane.WaitAsync(workCt).ConfigureAwait(false);
                try
                {
                    return await WriteDepthAsync(conn, id, depth, workCt);
                }
                finally
                {
                    writeLane.Release();
                }
            },
            ex =>
            {
                // A venue that pushed us away holds back every caller on this IP, not just this
                // collector: that is what a venue-wide gate is for.
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                {
                    _gate.Penalize();
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="workAsync"/> across <paramref name="items"/> with up to
    /// <paramref name="maxConcurrent"/> in flight, cancelling the rest of the herd as soon as one
    /// throws and re-throwing that failure — captured, not swallowed — once every worker has
    /// stopped. This is the whole fail-fast contract <see cref="RunAsync"/> depends on: a pass with
    /// a hole in it must come back as a thrown exception, never as a smaller "successful" count, or
    /// <c>CollectorLoop</c> reports ok=true and <c>ExchangeWorker.RecordGapAsync</c> never opens a
    /// <c>collector_gap</c> for an outage nobody saw.
    ///
    /// Generic over <typeparamref name="T"/>, and taking the failure hook as a delegate, purely so
    /// this contract can be pinned by a test that does not require Postgres — see
    /// <c>DepthCollectorSweepTests</c>. <see cref="RunAsync"/> is exactly this loop wired to venue
    /// leases and the database.
    /// </summary>
    internal static async Task<int> SweepAsync<T>(
        IReadOnlyList<T> items,
        int maxConcurrent,
        Func<T, CancellationToken, Task<int>> workAsync,
        Action<Exception> onItemFailed,
        CancellationToken ct)
    {
        // Cancels the remaining work as soon as one item fails — the pass is already doomed, and
        // continuing would spend venue budget on a result nobody will record.
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var next = -1;
        var written = 0;
        ExceptionDispatchInfo? failure = null;
        var failureLock = new object();

        async Task WorkAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= items.Count || failFast.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    Interlocked.Add(ref written, await workAsync(items[index], failFast.Token).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    // Recorded, not swallowed: below, once every worker has stopped, this is
                    // re-thrown rather than folded into the returned count. The first failure wins
                    // — it is the one that cancelled the others, so the siblings' cancellations
                    // cannot displace the real cause.
                    lock (failureLock)
                    {
                        failure ??= ExceptionDispatchInfo.Capture(ex);
                    }

                    onItemFailed(ex);

                    await failFast.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }

        var workers = new Task[Math.Min(maxConcurrent, items.Count)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = WorkAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        failure?.Throw();
        ct.ThrowIfCancellationRequested();
        return written;
    }

    /// <summary>Update only the depth columns; the row itself is owned by the snapshot writer, and
    /// affects zero rows harmlessly until the first snapshot for this instrument lands.</summary>
    private static Task<int> WriteDepthAsync(NpgsqlConnection conn, int id, Depth depth, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            update market_snapshot_latest set
                depth_bid_10bps = @Bid10, depth_ask_10bps = @Ask10,
                depth_bid_25bps = @Bid25, depth_ask_25bps = @Ask25,
                depth_bid_50bps = @Bid50, depth_ask_50bps = @Ask50,
                depth_at        = @At
             where exchange_instrument_id = @Id
            """,
            new
            {
                Id = id,
                Bid10 = depth.Bid10Bps,
                Ask10 = depth.Ask10Bps,
                Bid25 = depth.Bid25Bps,
                Ask25 = depth.Ask25Bps,
                Bid50 = depth.Bid50Bps,
                Ask50 = depth.Ask50Bps,
                depth.At,
            },
            cancellationToken: ct));
}

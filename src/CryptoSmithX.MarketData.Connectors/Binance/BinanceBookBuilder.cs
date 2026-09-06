using System.Collections.Concurrent;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// Maintains per-symbol Binance USDⓈ-M order books from the <c>@depth@100ms</c> diff stream. Same job
/// as <see cref="KrakenBookBuilder"/> and <see cref="Weex.WeexBookBuilder"/>, same strictness — a
/// dirty book yields no depth rather than a wrong one — and a sequencing rule that is neither of
/// theirs. Four venues, four rules, and they are worth seeing side by side because the differences
/// are exactly where a copied implementation would go quietly wrong:
///
///   * Kraken:  one <c>seq</c> per product; each delta must be exactly <c>seq + 1</c>.
///   * WEEX V3: a frame's <c>U</c> must EQUAL the previous frame's <c>u</c>, and the seeding snapshot
///     arrives on the socket itself, so there is no seam to get wrong.
///   * Binance SPOT: <c>U &lt;= lastUpdateId + 1 &lt;= u</c>, then <c>U = previous u + 1</c>.
///   * Binance USDⓈ-M — this class — has TWO rules, and using either one alone fails:
///       FIRST event after the REST seed:  <c>U &lt;= lastUpdateId &lt;= u</c>
///       every event after that:           <c>pu == the previous frame's u</c>
///
/// The first-event rule is not a softer version of the steady-state one; the two do not even look at
/// the same fields. The frame that straddles a seed carries a <c>pu</c> pointing at the frame before
/// it, which has nothing to do with the snapshot's <c>lastUpdateId</c>, so a builder that knew only
/// the <c>pu</c> rule would reject the seam frame, declare a gap, reseed, reject the next seam frame,
/// and never assemble a book at all. Both rules are pinned against the captured run in
/// Fixtures/binance-ws, whose 60 frames straddle a real REST snapshot: 8 of them predate it, the
/// ninth satisfies <c>U &lt;= lastUpdateId &lt;= u</c> and satisfies no <c>pu</c> relation to it, and
/// the remaining 51 chain by <c>pu</c> without a single break.
///
/// TWO MORE RULES LIVE HERE, both about not answering with a number we cannot stand behind:
///
///   * THE SEEDED WINDOW. The diff stream reports levels that CHANGED. A price level that sat
///     outside the REST seed and has not traded since is simply absent from this book — not zero,
///     absent — so a band summed across that region would be an undercount wearing a real number's
///     clothes. The seed's own price range is therefore recorded, and a band whose edge falls
///     outside it is answered null even when <see cref="DepthMath"/> would happily bound it from a
///     lone far level that a delta happened to deliver. On BTCUSDT the venue's deepest seed
///     (limit=1000) spans about 17 bps, so this is not a corner case: it is the normal state of the
///     most liquid symbol on the venue.
///   * A MALFORMED FRAME DIRTIES THE BOOK. It does not skip a side and carry on. The caller parses
///     levels and calls <see cref="MarkDirty"/> when parsing fails, because a frame half-applied and
///     then chained past is a book that is wrong WITHOUT being dirty — and nothing downstream can
///     ever notice that.
/// </summary>
public sealed class BinanceBookBuilder
{
    /// <summary>How many diff frames to hold per symbol while waiting for its REST seed. Binance's
    /// own documented procedure is "buffer the stream, then fetch the snapshot", and this is that
    /// buffer. It is small on purpose: at 100 ms frames a seed fetch of even a few seconds fits
    /// inside 64, and if it does not, the oldest frames are the ones that predate the snapshot and
    /// would have been dropped anyway. Overflow is therefore not an error — but a buffer that
    /// overflowed past the seam is, and that comes back as a gap.</summary>
    private const int PendingLimit = 64;

    private readonly ConcurrentDictionary<string, SymbolBook> _books = new(StringComparer.Ordinal);

    /// <summary>
    /// Seeds (or reseeds) a book from a REST snapshot, then replays whatever the socket delivered
    /// while that request was in flight.
    ///
    /// <paramref name="at"/> is OUR receive time, not the venue's: it is read back as this feed's
    /// freshness signal, and a venue clock that drifts must not be able to make every book look
    /// stale — or, worse, look fresh.
    ///
    /// Returns false when the replay found a gap — the buffer did not reach back far enough to hold
    /// the frame that straddles this snapshot, so the book is dirty again and wants another seed.
    /// Silently leaving that to be discovered by the next delta would spend a seed and then look
    /// like a healthy book for as long as the symbol stayed quiet.
    /// </summary>
    public bool ApplySnapshot(
        string symbol, long lastUpdateId,
        IReadOnlyList<(double Price, double Qty)> bids,
        IReadOnlyList<(double Price, double Qty)> asks,
        DateTimeOffset at)
    {
        var book = _books.GetOrAdd(symbol, _ => new SymbolBook());
        List<PendingFrame> replay;

        lock (book.Gate)
        {
            book.Bids.Clear();
            book.Asks.Clear();
            Apply(book.Bids, bids);
            Apply(book.Asks, asks);
            book.LastUpdateId = lastUpdateId;
            book.Dirty = false;
            book.Seeded = true;

            // The seam is ahead of us: the next frame to be applied — buffered or still to arrive —
            // is validated by the first-event rule, not by the pu chain.
            book.AwaitingFirstEvent = true;
            book.UpdatedAt = at;

            // The window this book is COMPLETE within, taken from the snapshot's own extent rather
            // than from the limit we asked for: the venue may return fewer levels than requested on
            // a thin symbol, and what matters is what actually arrived.
            book.SeedFloor = book.Bids.Count > 0 ? book.Bids.Keys.Min() : 0;
            book.SeedCeiling = book.Asks.Count > 0 ? book.Asks.Keys.Max() : double.MaxValue;

            replay = book.Pending;
            book.Pending = [];

            // Replayed INSIDE the lock, and that is not incidental. Seeding runs on the seed loop's
            // thread while frames arrive on the socket's; releasing the lock to replay would let a
            // live frame reach the book ahead of buffered frames that are older than it, which the
            // sequencing rules would then correctly report as a gap — a reseed defeated by its own
            // replay, at random, under load. The frames go through the ordinary application path so
            // the buffered ones obey exactly the same two rules as live ones; a second, parallel path
            // is how the seam quietly acquires two different meanings.
            var clean = true;
            foreach (var f in replay)
            {
                if (ApplyDeltaLocked(book, f.FirstUpdateId, f.LastUpdateId, f.PreviousUpdateId, f.Bids, f.Asks, at)
                    == DeltaResult.Gap)
                {
                    clean = false;
                }
            }

            return clean;
        }
    }

    /// <summary>
    /// Applies one <c>depthUpdate</c> frame, or buffers it when the book has not been seeded yet.
    /// <see cref="DeltaResult.Gap"/> means the stream we are reading is not the stream we think we
    /// are reading — the book is now dirty and the caller must reseed it from a fresh REST snapshot.
    /// </summary>
    public DeltaResult ApplyDelta(
        string symbol, long firstUpdateId, long lastUpdateId, long previousUpdateId,
        IReadOnlyList<(double Price, double Qty)> bids,
        IReadOnlyList<(double Price, double Qty)> asks,
        DateTimeOffset at)
    {
        var book = _books.GetOrAdd(symbol, _ => new SymbolBook());

        lock (book.Gate)
        {
            return ApplyDeltaLocked(book, firstUpdateId, lastUpdateId, previousUpdateId, bids, asks, at);
        }
    }

    /// <summary>The body of <see cref="ApplyDelta"/>, with the book's lock already held — so that
    /// <see cref="ApplySnapshot"/> can replay its buffer without ever letting go of it.</summary>
    private static DeltaResult ApplyDeltaLocked(
        SymbolBook book, long firstUpdateId, long lastUpdateId, long previousUpdateId,
        IReadOnlyList<(double Price, double Qty)> bids,
        IReadOnlyList<(double Price, double Qty)> asks,
        DateTimeOffset at)
    {
        {
            if (!book.Seeded)
            {
                // Buffered rather than dropped: Binance's procedure needs frames from BEFORE the
                // snapshot in hand, because the frame that straddles it is the one that starts the
                // chain, and by the time the snapshot arrives that frame is already in the past.
                if (book.Pending.Count >= PendingLimit)
                {
                    book.Pending.RemoveAt(0);
                }

                book.Pending.Add(new PendingFrame(firstUpdateId, lastUpdateId, previousUpdateId, bids, asks));
                return DeltaResult.Buffered;
            }

            if (book.Dirty)
            {
                return DeltaResult.Ignored;   // waiting on a reseed; applying now would compound the error
            }

            // Already contained in the snapshot. Not a gap and not an error — just work already done.
            if (lastUpdateId < book.LastUpdateId)
            {
                return DeltaResult.Ignored;
            }

            if (book.AwaitingFirstEvent)
            {
                // THE FIRST-EVENT RULE. The seam frame is the one whose range straddles the
                // snapshot's lastUpdateId; anything that starts after it means the frames in between
                // were missed while the snapshot was being fetched.
                if (firstUpdateId > book.LastUpdateId)
                {
                    book.Dirty = true;
                    return DeltaResult.Gap;
                }

                book.AwaitingFirstEvent = false;
            }
            else if (previousUpdateId != book.LastUpdateId)
            {
                // THE STEADY-STATE RULE. pu is the previous frame's u; anything else means frames
                // were missed, or duplicated, or belong to another stream. All three are the same
                // statement: this book can no longer be trusted.
                book.Dirty = true;
                return DeltaResult.Gap;
            }

            Apply(book.Bids, bids);
            Apply(book.Asks, asks);
            book.LastUpdateId = lastUpdateId;
            book.UpdatedAt = at;
            return DeltaResult.Applied;
        }
    }

    public void MarkDirty(string symbol)
    {
        if (_books.TryGetValue(symbol, out var book))
        {
            lock (book.Gate) { book.Dirty = true; }
        }
    }

    /// <summary>
    /// Distrusts every book at once. Called on reconnect BEFORE resubscribing, and the ordering is
    /// the whole point: <see cref="Streaming.WsConnection"/> reconnects after roughly a second and
    /// flips <c>Connected</c> true the instant the socket opens, while this builder still holds every
    /// book exactly as it stood before the drop. Whatever was missed during the outage is by
    /// definition a sequence break we can never observe, because the frame that would have proved it
    /// never arrived.
    /// </summary>
    public void MarkAllDirty()
    {
        foreach (var book in _books.Values)
        {
            lock (book.Gate) { book.Dirty = true; }
        }
    }

    /// <summary>Dirty, unseeded or absent — everything that means "reseed this from REST".</summary>
    public bool NeedsSeed(string symbol) =>
        !_books.TryGetValue(symbol, out var book) || NeedsSeedLocked(book);

    public void Remove(string symbol) => _books.TryRemove(symbol, out _);

    /// <summary>How many books are clean, seeded and updated within <paramref name="maxAge"/> of
    /// <paramref name="now"/>. A component of the feed's health, never a per-symbol gate — see
    /// <see cref="TryGetDepth"/> for why a quiet book is still a correct one.</summary>
    public int FreshCount(TimeSpan maxAge, DateTimeOffset now)
    {
        var cutoff = now - maxAge;
        var n = 0;
        foreach (var book in _books.Values)
        {
            lock (book.Gate)
            {
                if (book.Seeded && !book.Dirty && book.UpdatedAt >= cutoff)
                {
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>
    /// Depth from a clean, seeded book, stamped <paramref name="asOf"/>; false when the book is
    /// dirty, unseeded, absent, or has no two-sided top.
    ///
    /// Age is not a gate, exactly as on Kraken and WEEX: a book that has simply been quiet is still
    /// correct, and the caller stamps the read time because a live socket confirms the book is
    /// current now. Socket liveness is the feed's concern, not the book's.
    ///
    /// What IS a gate is completeness. Each band is answered only if its edge price lies inside the
    /// price range the REST seed actually delivered — see the class remarks. A band outside that
    /// range comes back null even when the levels present would let <see cref="DepthMath"/> bound it,
    /// because "bounded" there means "some level lies beyond the edge", which a single far level
    /// delivered by a delta satisfies while the region in between is still unknown to us.
    /// </summary>
    public bool TryGetDepth(string symbol, DateTimeOffset asOf, out Depth depth)
    {
        depth = default!;
        if (!_books.TryGetValue(symbol, out var book))
        {
            return false;
        }

        List<(double, double)> bids, asks;
        double floor, ceiling;
        lock (book.Gate)
        {
            if (!book.Seeded || book.Dirty)
            {
                return false;
            }

            bids = Snapshot(book.Bids);
            asks = Snapshot(book.Asks);
            floor = book.SeedFloor;
            ceiling = book.SeedCeiling;
        }

        var computed = DepthMath.Compute(bids, asks, asOf);
        if (computed is null)
        {
            return false;
        }

        var mid = Mid(bids, asks);
        depth = new Depth(
            Bid10Bps: BidCovered(mid, 10, floor) ? computed.Bid10Bps : null,
            Ask10Bps: AskCovered(mid, 10, ceiling) ? computed.Ask10Bps : null,
            Bid25Bps: BidCovered(mid, 25, floor) ? computed.Bid25Bps : null,
            Ask25Bps: AskCovered(mid, 25, ceiling) ? computed.Ask25Bps : null,
            Bid50Bps: BidCovered(mid, 50, floor) ? computed.Bid50Bps : null,
            Ask50Bps: AskCovered(mid, 50, ceiling) ? computed.Ask50Bps : null,
            At: computed.At);

        return true;
    }

    /// <summary>True when the seeded window no longer covers the widest band around the CURRENT mid —
    /// the price has walked out from under the snapshot and the deep bands have gone dark. The feed
    /// uses this to ask for a fresh seed, debounced; it is deliberately not a reason to stop serving,
    /// because the narrow bands are usually still covered and still true.</summary>
    public bool SeedWindowOutgrown(string symbol)
    {
        if (!_books.TryGetValue(symbol, out var book))
        {
            return false;
        }

        lock (book.Gate)
        {
            if (!book.Seeded || book.Dirty || book.Bids.Count == 0 || book.Asks.Count == 0)
            {
                return false;
            }

            var mid = (book.Bids.Keys.Max() + book.Asks.Keys.Min()) / 2;
            return !BidCovered(mid, 50, book.SeedFloor) || !AskCovered(mid, 50, book.SeedCeiling);
        }
    }

    /// <summary>Mid of the top of a clean, seeded book — for the REST cross-check, which compares this
    /// against the venue's own batched book ticker to catch a book that has frozen behind a socket
    /// that still looks alive. False if not measurable.</summary>
    public bool TryGetTopMid(string symbol, out double mid)
    {
        mid = 0;
        if (!_books.TryGetValue(symbol, out var book))
        {
            return false;
        }

        lock (book.Gate)
        {
            if (!book.Seeded || book.Dirty || book.Bids.Count == 0 || book.Asks.Count == 0)
            {
                return false;
            }

            mid = (book.Bids.Keys.Max() + book.Asks.Keys.Min()) / 2;
            return mid > 0;
        }
    }

    private static bool BidCovered(double mid, int bps, double seedFloor) =>
        seedFloor > 0 && mid * (1 - (bps / 10_000.0)) >= seedFloor;

    private static bool AskCovered(double mid, int bps, double seedCeiling) =>
        seedCeiling < double.MaxValue && mid * (1 + (bps / 10_000.0)) <= seedCeiling;

    private static double Mid(List<(double Price, double Qty)> bids, List<(double Price, double Qty)> asks)
    {
        var bestBid = 0.0;
        foreach (var (price, qty) in bids) { if (qty > 0 && price > bestBid) { bestBid = price; } }
        var bestAsk = double.MaxValue;
        foreach (var (price, qty) in asks) { if (qty > 0 && price < bestAsk) { bestAsk = price; } }
        return (bestBid + bestAsk) / 2;
    }

    /// <summary>A quantity of exactly "0" is a removal of that price level, not a level standing at
    /// zero size — the same convention WEEX uses, and asserted against the captured frames rather
    /// than assumed.</summary>
    private static void Apply(Dictionary<double, double> side, IReadOnlyList<(double Price, double Qty)> levels)
    {
        foreach (var (price, qty) in levels)
        {
            if (qty > 0) { side[price] = qty; } else { side.Remove(price); }
        }
    }

    private static List<(double, double)> Snapshot(Dictionary<double, double> side)
    {
        var list = new List<(double, double)>(side.Count);
        foreach (var kv in side) { list.Add((kv.Key, kv.Value)); }
        return list;
    }

    private static bool NeedsSeedLocked(SymbolBook book)
    {
        lock (book.Gate) { return !book.Seeded || book.Dirty; }
    }

    public enum DeltaResult
    {
        Applied,

        /// <summary>Held until the REST seed lands. Not an error — it is step one of the venue's own
        /// documented procedure.</summary>
        Buffered,

        /// <summary>Work already contained in the seed, or a book waiting on a reseed.</summary>
        Ignored,

        /// <summary>Frames were missed. The book is dirty and must be reseeded.</summary>
        Gap,
    }

    private readonly record struct PendingFrame(
        long FirstUpdateId,
        long LastUpdateId,
        long PreviousUpdateId,
        IReadOnlyList<(double Price, double Qty)> Bids,
        IReadOnlyList<(double Price, double Qty)> Asks);

    private sealed class SymbolBook
    {
        public readonly object Gate = new();
        public readonly Dictionary<double, double> Bids = new();
        public readonly Dictionary<double, double> Asks = new();

        /// <summary>The <c>u</c> of the last frame applied — what the next frame's <c>pu</c> must
        /// equal, once the seam is behind us.</summary>
        public long LastUpdateId;

        /// <summary>True between the REST seed and the frame that straddles it. The one flag that
        /// selects between this venue's two different sequencing rules.</summary>
        public bool AwaitingFirstEvent;

        /// <summary>The lowest bid and highest ask the seed delivered: the price range within which
        /// this book is COMPLETE, as opposed to merely populated.</summary>
        public double SeedFloor;
        public double SeedCeiling = double.MaxValue;

        public bool Dirty;
        public bool Seeded;

        /// <summary>Frames that arrived before the seed, in arrival order.</summary>
        public List<PendingFrame> Pending = [];

        /// <summary>Our receive time for the last applied frame — see <see cref="ApplySnapshot"/>.</summary>
        public DateTimeOffset UpdatedAt;
    }
}

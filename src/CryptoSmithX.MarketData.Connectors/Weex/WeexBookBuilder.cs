using System.Collections.Concurrent;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// Maintains per-symbol WEEX order books from the V3 <c>@depth</c> stream. Same job as
/// <see cref="KrakenBookBuilder"/> and deliberately the same strictness — a dirty book yields no
/// depth rather than a wrong one — but the sequencing rule is WEEX's, and it is not Kraken's and not
/// Binance's either:
///
///   * Kraken: one <c>seq</c> per product, each delta must be exactly <c>seq + 1</c>.
///   * Binance: <c>U &lt;= lastUpdateId + 1 &lt;= u</c> against a REST-seeded book.
///   * WEEX V3: a frame's <c>U</c> must EQUAL the last applied frame's <c>u</c> — an exact equality,
///     not an off-by-one — and the run is seeded by the <c>u</c> of the <c>depthSnapshot</c> the
///     socket itself delivers.
///
/// That last difference is the whole reason this class is not a copy of either: WEEX puts the
/// snapshot on the socket, so there is no REST-seed race to lose and no first-event special case to
/// implement. The rule is pinned by <c>WeexWsProtocolTests</c> against 60 consecutive captured
/// frames (Fixtures/weex-ws), so if WEEX ever ships a fourth generation the fixtures fail before
/// this code silently misapplies deltas.
///
/// Two further WEEX-shaped rules live here:
///
///   * A frame carries its own depth level in <c>l</c>, and a book is only served when that level is
///     at least the one we subscribed to. The socket's own window is the only guarantee we have
///     about how far the book reaches; serving a thinner book than the REST path returns would
///     quietly downgrade every band the venue no longer reaches (see <see cref="DepthMath"/>, which
///     nulls a band it cannot bound).
///   * The book is trimmed back to that window. Throughout the captured run WEEX did emit an
///     explicit removal for every level that left the window — both sides end at exactly 15 levels
///     after 60 frames — but nothing promises it, and an untrimmed book would accumulate levels the
///     venue has stopped maintaining. Those stale levels sit exactly where the 50 bps band is
///     measured, so the cost of being wrong about this is a silently inflated depth number.
/// </summary>
public sealed class WeexBookBuilder
{
    private readonly ConcurrentDictionary<string, SymbolBook> _books = new(StringComparer.Ordinal);
    private readonly int _minLevels;

    /// <param name="minLevels">The depth level we subscribed to. A book whose frames arrive thinner
    /// than this is kept in sequence but never served as depth.</param>
    public WeexBookBuilder(int minLevels) => _minLevels = minLevels;

    /// <summary>Seeds (or reseeds) a book from a <c>depthSnapshot</c> frame, clearing any dirty flag.
    /// <paramref name="at"/> is OUR receive time, not the venue's <c>E</c>: it is read back as the
    /// freshness signal for this feed, and a venue clock that drifts must not be able to make every
    /// book look stale — or, worse, look fresh.</summary>
    public void ApplySnapshot(
        string symbol, long lastUpdateId, int levels,
        IReadOnlyList<(double Price, double Qty)> bids,
        IReadOnlyList<(double Price, double Qty)> asks,
        DateTimeOffset at)
    {
        var book = _books.GetOrAdd(symbol, _ => new SymbolBook());
        lock (book.Gate)
        {
            book.Bids.Clear();
            book.Asks.Clear();
            Apply(book.Bids, bids);
            Apply(book.Asks, asks);
            book.LastUpdateId = lastUpdateId;
            book.Levels = levels;
            book.Dirty = false;
            book.Seeded = true;
            book.UpdatedAt = at;
        }
    }

    /// <summary>Applies one <c>depth</c> frame. <see cref="DeltaResult.Gap"/> means
    /// <paramref name="firstUpdateId"/> did not match the last applied <c>u</c> — frames were missed,
    /// the book is now dirty, and the caller must resync it from a fresh snapshot.</summary>
    public DeltaResult ApplyDelta(
        string symbol, long firstUpdateId, long lastUpdateId, int levels,
        IReadOnlyList<(double Price, double Qty)> bids,
        IReadOnlyList<(double Price, double Qty)> asks,
        DateTimeOffset at)
    {
        if (!_books.TryGetValue(symbol, out var book))
        {
            return DeltaResult.Ignored;   // a delta before the snapshot; wait for the snapshot
        }

        lock (book.Gate)
        {
            if (!book.Seeded || book.Dirty)
            {
                return DeltaResult.Ignored;
            }

            // The WEEX rule, in one line: U equals the previous frame's u exactly. A frame that
            // repeats work already applied is as much a break as one that skips work — both mean the
            // stream we are reading is not the stream we think we are reading.
            if (firstUpdateId != book.LastUpdateId)
            {
                book.Dirty = true;
                return DeltaResult.Gap;
            }

            Apply(book.Bids, bids);
            Apply(book.Asks, asks);
            book.LastUpdateId = lastUpdateId;
            book.Levels = levels;
            book.UpdatedAt = at;
            Trim(book.Bids, levels, bestFirst: (a, b) => b.CompareTo(a));
            Trim(book.Asks, levels, bestFirst: (a, b) => a.CompareTo(b));
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
    /// book exactly as it stood before the drop. Between the reconnect and the first fresh snapshot
    /// those books are pure fiction — the venue moved and nobody told us — and the feed's health
    /// check, which reads socket state, would happily serve them as live. Whatever was missed while
    /// the socket was down is by definition a sequence gap we can never observe, because the frame
    /// that would have proved it never arrived.
    /// </summary>
    public void MarkAllDirty()
    {
        foreach (var book in _books.Values)
        {
            lock (book.Gate) { book.Dirty = true; }
        }
    }

    /// <summary>Dirty, unseeded, or absent — anything that means "do not trust this book".</summary>
    public bool IsDirty(string symbol) =>
        !_books.TryGetValue(symbol, out var book) || IsDirtyLocked(book);

    public void Remove(string symbol) => _books.TryRemove(symbol, out _);

    /// <summary>
    /// How many books are clean, seeded, thick enough to serve, and have been updated within
    /// <paramref name="maxAge"/> of <paramref name="now"/> — this feed's liveness signal.
    ///
    /// Kraken counts fresh entries in its ticker cache for this; WEEX has no ticker cache to count,
    /// because its socket offers no top-of-book channel at all, so the depth stream is the only
    /// evidence that the connection is doing anything. The count is a feed-wide health signal and
    /// NOT a per-symbol gate: see <see cref="TryGetDepth"/> for why age does not disqualify an
    /// individual book.
    /// </summary>
    public int FreshCount(TimeSpan maxAge, DateTimeOffset now)
    {
        var cutoff = now - maxAge;
        var n = 0;
        foreach (var book in _books.Values)
        {
            lock (book.Gate)
            {
                if (book.Seeded && !book.Dirty && book.Levels >= _minLevels && book.UpdatedAt >= cutoff)
                {
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>Depth from a clean, seeded, full-depth book, stamped <paramref name="asOf"/>; false
    /// when the book is dirty, thin or absent. Age is not a gate here, exactly as on Kraken: a book
    /// that has simply been quiet (an illiquid instrument, no recent frame) is still correct, and the
    /// caller stamps the read time because a live socket confirms the book is current now. Socket
    /// liveness is the feed's concern — it gates depth on overall health and cross-checks against
    /// REST — not the book's.</summary>
    public bool TryGetDepth(string symbol, DateTimeOffset asOf, out Depth depth)
    {
        depth = default!;
        if (!_books.TryGetValue(symbol, out var book))
        {
            return false;
        }

        List<(double, double)> bids, asks;
        lock (book.Gate)
        {
            if (!book.Seeded || book.Dirty || book.Levels < _minLevels)
            {
                return false;
            }

            bids = Snapshot(book.Bids);
            asks = Snapshot(book.Asks);
        }

        var computed = DepthMath.Compute(bids, asks, asOf);
        if (computed is null)
        {
            return false;
        }

        depth = computed;
        return true;
    }

    /// <summary>Mid of the top of a clean, seeded book — for the REST cross-check, which compares
    /// this against the venue's own batched ticker to catch a book that has frozen behind a socket
    /// that still looks alive. Unlike <see cref="TryGetDepth"/> this does not require the full depth
    /// level: the top of a thin book is still a true top, and a frozen thin book is still worth
    /// catching. False if not measurable.</summary>
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

            var bestBid = double.MinValue;
            foreach (var price in book.Bids.Keys) { if (price > bestBid) { bestBid = price; } }
            var bestAsk = double.MaxValue;
            foreach (var price in book.Asks.Keys) { if (price < bestAsk) { bestAsk = price; } }
            mid = (bestBid + bestAsk) / 2;
            return mid > 0;
        }
    }

    /// <summary>A quantity of exactly "0" is a removal of that price level, not a level standing at
    /// zero size — captured and asserted, not assumed (see WeexWsProtocolTests).</summary>
    private static void Apply(Dictionary<double, double> side, IReadOnlyList<(double Price, double Qty)> levels)
    {
        foreach (var (price, qty) in levels)
        {
            if (qty > 0) { side[price] = qty; } else { side.Remove(price); }
        }
    }

    /// <summary>
    /// Cuts a side back to the venue's own window, worst prices first. Only when the side has grown
    /// half again past the window: the sort is O(n log n) and this runs on every frame of every
    /// symbol, so paying it each time would be paying it ~2000 times a second for nothing. The slack
    /// bounds the error instead — a level that has fallen out of the window can linger for a while,
    /// but it can never accumulate, and while it lingers it sits below <paramref name="levels"/>
    /// deep, where the 10/25/50 bps bands do not reach on any symbol thick enough for the venue to
    /// be maintaining 200 levels of it.
    /// </summary>
    private static void Trim(Dictionary<double, double> side, int levels, Comparison<double> bestFirst)
    {
        if (levels <= 0 || side.Count <= levels + (levels / 2))
        {
            return;
        }

        var prices = new List<double>(side.Keys);
        prices.Sort(bestFirst);
        for (var i = levels; i < prices.Count; i++)
        {
            side.Remove(prices[i]);
        }
    }

    private static List<(double, double)> Snapshot(Dictionary<double, double> side)
    {
        var list = new List<(double, double)>(side.Count);
        foreach (var kv in side) { list.Add((kv.Key, kv.Value)); }
        return list;
    }

    private static bool IsDirtyLocked(SymbolBook book)
    {
        lock (book.Gate) { return book.Dirty || !book.Seeded; }
    }

    public enum DeltaResult
    {
        Applied,
        Ignored,
        Gap,
    }

    private sealed class SymbolBook
    {
        public readonly object Gate = new();
        public readonly Dictionary<double, double> Bids = new();
        public readonly Dictionary<double, double> Asks = new();

        /// <summary>The <c>u</c> of the last frame applied — what the next frame's <c>U</c> must equal.</summary>
        public long LastUpdateId;

        /// <summary>The <c>l</c> the venue stamped on that frame: how deep this stream reaches.</summary>
        public int Levels;

        public bool Dirty;
        public bool Seeded;

        /// <summary>Our receive time for the last applied frame — see <see cref="ApplySnapshot"/>.</summary>
        public DateTimeOffset UpdatedAt;
    }
}

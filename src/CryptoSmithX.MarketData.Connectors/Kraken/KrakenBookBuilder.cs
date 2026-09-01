using System.Collections.Concurrent;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

/// <summary>
/// Maintains per-symbol Kraken Futures order books from the WS <c>book</c> feed: a snapshot seeds it,
/// deltas mutate it, and a single per-product <c>seq</c> must advance by exactly one each time. A gap
/// means we missed a message and the book can no longer be trusted, so it is marked dirty and the
/// feed resyncs (a fresh snapshot clears the flag). Depth is read off the live book with the same
/// <see cref="DepthMath"/> the REST path uses. This is the bug-prone heart of the WS path, so its
/// rules are deliberately strict: a dirty or stale book yields no depth rather than a wrong one.
/// </summary>
public sealed class KrakenBookBuilder
{
    private readonly ConcurrentDictionary<string, SymbolBook> _books = new(StringComparer.Ordinal);

    public void ApplySnapshot(
        string symbol, long seq,
        IReadOnlyList<(double Price, double Qty)> bids,
        IReadOnlyList<(double Price, double Qty)> asks,
        DateTimeOffset at)
    {
        var book = _books.GetOrAdd(symbol, _ => new SymbolBook());
        lock (book.Gate)
        {
            book.Bids.Clear();
            book.Asks.Clear();
            foreach (var (price, qty) in bids)
            {
                if (qty > 0) { book.Bids[price] = qty; }
            }

            foreach (var (price, qty) in asks)
            {
                if (qty > 0) { book.Asks[price] = qty; }
            }

            book.Seq = seq;
            book.Dirty = false;
            book.Seeded = true;
            book.UpdatedAt = at;
        }
    }

    /// <summary>Apply one delta. <see cref="DeltaResult.Gap"/> means the sequence broke and the book
    /// is now dirty — the caller must resync.</summary>
    public DeltaResult ApplyDelta(string symbol, bool isBid, long seq, double price, double qty, DateTimeOffset at)
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

            if (seq != book.Seq + 1)
            {
                book.Dirty = true;
                return DeltaResult.Gap;
            }

            var side = isBid ? book.Bids : book.Asks;
            if (qty > 0) { side[price] = qty; } else { side.Remove(price); }
            book.Seq = seq;
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

    /// <summary>Dirty, unseeded, or absent — anything that means "do not trust the book".</summary>
    public bool IsDirty(string symbol) =>
        !_books.TryGetValue(symbol, out var book) || IsDirtyLocked(book);

    public void Remove(string symbol) => _books.TryRemove(symbol, out _);

    /// <summary>Depth from a clean, seeded book, stamped <paramref name="asOf"/>; false when the book
    /// is dirty or absent. Age is not a gate: a book that has simply been quiet (an illiquid
    /// instrument, no recent delta) is still correct, and the caller stamps the read time because a
    /// live socket confirms the book is current now. Socket liveness is the feed's concern — it gates
    /// depth on overall health, not per-book age.</summary>
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
            if (!book.Seeded || book.Dirty)
            {
                return false;
            }

            bids = new List<(double, double)>(book.Bids.Count);
            foreach (var kv in book.Bids) { bids.Add((kv.Key, kv.Value)); }
            asks = new List<(double, double)>(book.Asks.Count);
            foreach (var kv in book.Asks) { asks.Add((kv.Key, kv.Value)); }
        }

        var computed = DepthMath.Compute(bids, asks, asOf);
        if (computed is null)
        {
            return false;
        }

        depth = computed;
        return true;
    }

    /// <summary>Mid of the top of a clean, seeded book — for the REST cross-check, which compares this
    /// against REST to catch a book that has frozen behind a live socket. False if not measurable.</summary>
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
        public long Seq;
        public bool Dirty;
        public bool Seeded;
        public DateTimeOffset UpdatedAt;
    }
}

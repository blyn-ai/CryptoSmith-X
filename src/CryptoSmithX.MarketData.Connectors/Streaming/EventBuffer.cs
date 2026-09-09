using System.Collections.Concurrent;

namespace CryptoSmithX.MarketData.Connectors.Streaming;

/// <summary>
/// What a push feed needs and <see cref="MarketCache{T}"/> cannot be: an APPEND-only hold for events
/// that all matter, drained whole by a collector on its own schedule.
///
/// <see cref="MarketCache{T}"/> keeps the latest value per symbol — right for a ticker, wrong for a
/// trade feed, where the second trade does not replace the first. Nothing in this solution buffered
/// events before this class: every writer polled a current state on a timer. A trade or liquidation
/// arrives as a discrete push with no "current value" to poll, so the socket appends here and
/// <c>TradeCollector</c> takes everything since its last pass in one go.
///
/// BOUNDED, AND IT DROPS THE OLDEST. A socket that outruns its drain must not take the process down
/// with it: past <paramref name="capacity"/> the oldest event is discarded and counted. Dropping the
/// oldest rather than the newest keeps the buffer showing the most recent market, which is the half
/// a late reader can still act on; <see cref="Dropped"/> is what says the loss happened at all, and
/// the collector logs it rather than letting it pass silently.
/// </summary>
public sealed class EventBuffer<T>(int capacity = 200_000)
{
    private readonly ConcurrentQueue<T> _queue = new();
    private long _dropped;
    private int _count;

    /// <summary>How many events were discarded for want of room since the process started. Never
    /// resets: a rate is what a reader wants here, and a counter that resets on drain would hide a
    /// steady trickle of loss behind a zero.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public int Count => Volatile.Read(ref _count);

    public void Add(T item)
    {
        _queue.Enqueue(item);
        if (Interlocked.Increment(ref _count) <= capacity)
        {
            return;
        }

        // Over capacity: shed from the head. The loop rather than a single dequeue because several
        // producers can cross the line at once, and each of them owes exactly one eviction.
        if (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Everything buffered right now, oldest first. Events appended DURING the drain stay
    /// for the next one rather than extending this pass — a socket that never goes quiet would
    /// otherwise keep one drain running forever.</summary>
    public IReadOnlyList<T> Drain()
    {
        var taking = Volatile.Read(ref _count);
        if (taking == 0)
        {
            return [];
        }

        var drained = new List<T>(taking);
        while (drained.Count < taking && _queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            drained.Add(item);
        }

        return drained;
    }
}

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

    /// <summary>
    /// The observers, as an immutable array replaced whole rather than a collection mutated in
    /// place. <see cref="Add"/> reads this reference exactly once and then works against the array
    /// it got, so subscribing and unsubscribing never block a socket's read loop and a writer
    /// mid-append simply finishes against the list as it stood. Copy-on-write is affordable
    /// precisely because subscribers are counted in tens and events in hundreds of thousands.
    /// </summary>
    private volatile EventTap<T>[] _taps = [];

    /// <summary>How many events were discarded for want of room since the process started. Never
    /// resets: a rate is what a reader wants here, and a counter that resets on drain would hide a
    /// steady trickle of loss behind a zero.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public int Count => Volatile.Read(ref _count);

    public void Add(T item)
    {
        _queue.Enqueue(item);

        // The observers are served from the socket's own thread, without a lock and without waiting
        // for any of them: each one owns a bounded queue and drops for itself. Handing this thread
        // to subscriber code — a callback, a channel that can block — would let one slow viewer
        // stall the feed that every other subscriber and the database read from.
        var taps = _taps;
        foreach (var tap in taps)
        {
            tap.Offer(item);
        }

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

    /// <summary>
    /// A second, LOSSY reader of this feed, with its own queue and its own bound.
    ///
    /// It exists because <see cref="Drain"/> cannot be shared: it removes what it returns, so a
    /// second caller would not be a second reader but a thief, taking events the database never
    /// sees. The split is deliberate all the way down — the recorder stays lossless and authoritative,
    /// a live view is allowed to miss a print, and neither can degrade the other.
    ///
    /// <paramref name="capacity"/> is the viewer's own tolerance for falling behind: past it the
    /// oldest print is shed and counted, exactly as this buffer does for itself.
    /// </summary>
    public EventTap<T> Observe(int capacity)
    {
        var tap = new EventTap<T>(capacity, Unsubscribe);

        EventTap<T>[] current, next;
        do
        {
            current = _taps;
            next = [.. current, tap];
        }
        while (Interlocked.CompareExchange(ref _taps, next, current) != current);

        return tap;
    }

    private void Unsubscribe(EventTap<T> tap)
    {
        EventTap<T>[] current, next;
        do
        {
            current = _taps;
            if (Array.IndexOf(current, tap) < 0)
            {
                return;
            }

            next = [.. current.Where(t => t != tap)];
        }
        while (Interlocked.CompareExchange(ref _taps, next, current) != current);
    }
}

/// <summary>
/// One subscriber's view of an <see cref="EventBuffer{T}"/>: everything appended since it started
/// watching, up to its own bound, and never anything the recorder needed.
///
/// Bounded and oldest-shed, like the buffer itself, and for the same reason — but the consequence
/// here is the opposite one. The buffer drops because the process must survive a fast socket; a tap
/// drops because a viewer must never be able to slow the socket down. What it costs is a gap in
/// somebody's tape, and <see cref="Dropped"/> is what makes that gap sayable rather than silent.
/// </summary>
public sealed class EventTap<T> : IDisposable
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly int _capacity;
    private readonly Action<EventTap<T>> _unsubscribe;
    private long _dropped;
    private int _count;
    private volatile bool _closed;

    internal EventTap(int capacity, Action<EventTap<T>> unsubscribe)
    {
        _capacity = capacity;
        _unsubscribe = unsubscribe;
    }

    /// <summary>How many events this subscriber missed for want of room. Never resets, so a steady
    /// trickle of loss cannot hide behind a zero.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public int Count => Volatile.Read(ref _count);

    /// <summary>Everything this subscriber has not read yet, oldest first.</summary>
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

    /// <summary>Called from the feed's own thread for every appended event. Never blocks and never
    /// throws back into it.</summary>
    internal void Offer(T item)
    {
        if (_closed)
        {
            return;
        }

        _queue.Enqueue(item);
        if (Interlocked.Increment(ref _count) <= _capacity)
        {
            return;
        }

        if (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Stops delivery. What was already held stays readable — a viewer that closed mid-read
    /// is not a reason to throw away what it was reading — but nothing further is queued behind a
    /// subscriber nobody is coming back for.</summary>
    public void Dispose()
    {
        _closed = true;
        _unsubscribe(this);
    }
}

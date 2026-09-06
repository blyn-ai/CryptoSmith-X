namespace CryptoSmithX.MarketData.Connectors.Streaming;

/// <summary>
/// A short history of a <see cref="WsConnection"/>'s successive connections — each one's epoch, when
/// it opened, when it was replaced, and how many frames it delivered — kept so that anything asking a
/// question about "the connection" can name WHICH connection it means.
///
/// WHY THIS EXISTS, and it is not tidiness. A feed that arms a delayed check on connect and then reads
/// a counter on a timer is reading a counter that a reconnect has since zeroed for a DIFFERENT socket.
/// That is not hypothetical: <see cref="Binance.BinanceWsFeed"/>'s startup-liveness check did exactly
/// this and reported "subscribed 566 symbols and received NOTHING in 15s" about a connection that had
/// delivered forty thousand frames, because its successor was 1.2 s old when the watcher woke. The
/// alarm text then sent the reader to the ws_url setting, which was correct all along, and the actual
/// symptom — that connections were being replaced every few seconds — had no detector of its own at
/// all. An epoch handed out at connect and compared after the delay is what makes the two questions
/// separable: "this socket is silent" and "this socket did not survive long enough to be asked".
///
/// The frame count arrives at <see cref="Open"/> rather than being incremented here, so the receive
/// path keeps its single interlocked increment on a plain field and never takes this lock: the caller
/// swaps its own hot counter to zero and hands over what it read, which closes the outgoing run and
/// opens the new one in the same step and cannot lose a frame between the two.
/// </summary>
internal sealed class ConnectionLog
{
    /// <summary>How far back <see cref="CountOpenedWithin"/> can be asked about. An hour is the window
    /// a reconnect rate is worth judging over on a venue that closes a healthy public stream once a
    /// day; runs older than this answer no question anyone asks.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    /// <summary>A hard ceiling on top of <see cref="Retention"/>, because the thing this class exists
    /// to describe is a reconnect storm and a storm has no upper bound. Time alone bounds the list at
    /// thirty entries for the incident we saw and at thirty thousand for one an order of magnitude
    /// worse — on a list that <see cref="TryGet"/> scans. Five hundred is far more history than any
    /// question here needs, and the counts stay honest because they are capped by the same window.</summary>
    private const int MaxRuns = 512;

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly List<Run> _runs = [];

    private long _epoch;

    public ConnectionLog(TimeProvider clock) => _clock = clock;

    /// <summary>The epoch of the connection currently believed to be live. Zero before the first
    /// <see cref="Open"/>.</summary>
    public long Current
    {
        get { lock (_gate) { return _epoch; } }
    }

    /// <summary>Closes the outgoing run with the frame count it ended on and opens a new one, whose
    /// epoch is returned. Call it once per connect, from the connect callback.</summary>
    public long Open(long framesOnTheOutgoingRun)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_runs.Count > 0 && _runs[^1].ClosedAt is null)
            {
                _runs[^1] = _runs[^1] with { ClosedAt = now, Frames = framesOnTheOutgoingRun };
            }

            _runs.RemoveAll(r => now - r.OpenedAt > Retention);
            if (_runs.Count >= MaxRuns)
            {
                _runs.RemoveRange(0, _runs.Count - MaxRuns + 1);
            }

            _epoch++;
            _runs.Add(new Run(_epoch, now, null, 0));
            return _epoch;
        }
    }

    /// <summary>The run with this epoch, or false once it has aged out of the retention window.</summary>
    public bool TryGet(long epoch, out Run run)
    {
        lock (_gate)
        {
            foreach (var candidate in _runs)
            {
                if (candidate.Epoch == epoch)
                {
                    run = candidate;
                    return true;
                }
            }
        }

        run = default;
        return false;
    }

    /// <summary>How many connections were opened in the last <paramref name="window"/> — the reconnect
    /// RATE, which is the thing worth alarming on. Capped by <see cref="Retention"/>.</summary>
    public int CountOpenedWithin(TimeSpan window)
    {
        var since = _clock.GetUtcNow() - window;
        lock (_gate)
        {
            var count = 0;
            foreach (var run in _runs)
            {
                if (run.OpenedAt >= since)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// What a delayed liveness check armed on connection <paramref name="epoch"/> is entitled to
    /// conclude now. The whole point of this method is that there are FOUR answers and the code that
    /// regressed had two, so "we were replaced" came out as "we received nothing".
    ///
    /// <paramref name="framesReadBeforeThisCall"/> must be read by the caller BEFORE calling — the
    /// ordering is the lock-free part. A reconnect landing between that read and this call zeroes the
    /// counter but also moves the epoch, so the stale count is rejected here rather than believed; a
    /// reconnect landing after both cannot have touched the value that was read.
    /// </summary>
    public ConnectionVerdict Judge(long epoch, long framesReadBeforeThisCall, bool connected)
    {
        // Checked first and unconditionally: a count belonging to a different connection tells us
        // nothing about this one, in either direction.
        if (Current != epoch)
        {
            return ConnectionVerdict.Replaced;
        }

        if (framesReadBeforeThisCall > 0)
        {
            return ConnectionVerdict.Live;
        }

        // Still the current epoch but the socket is gone and the reconnect has not come round yet.
        // Silence was never established — the connection simply stopped existing.
        return connected ? ConnectionVerdict.Silent : ConnectionVerdict.Dropped;
    }

    /// <summary>One connection's life. <see cref="ClosedAt"/> is null while it is the live one, and
    /// <see cref="Frames"/> is only final once it is not — an open run reports zero because its count
    /// still lives on the receive path's own field.</summary>
    internal readonly record struct Run(long Epoch, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, long Frames);
}

/// <summary>
/// What is known about one connection after a delayed check. Only <see cref="Silent"/> is evidence of
/// a misrouted stream; the other three are evidence of nothing, or of a different problem entirely,
/// and must not borrow its message.
/// </summary>
internal enum ConnectionVerdict
{
    /// <summary>It received frames. Nothing to say.</summary>
    Live,

    /// <summary>A later connection has replaced it. Whether it was silent is UNKNOWABLE now — its
    /// counter has been zeroed for its successor — but that it did not survive the check window is
    /// itself worth reporting.</summary>
    Replaced,

    /// <summary>Still the current epoch, but the socket is no longer open and no reconnect has run
    /// yet. Silence was never established.</summary>
    Dropped,

    /// <summary>Still the current epoch, still open, and not one frame. This is the only state that
    /// means what "received NOTHING" is supposed to mean.</summary>
    Silent,
}

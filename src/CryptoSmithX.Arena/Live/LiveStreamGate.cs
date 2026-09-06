namespace CryptoSmithX.Arena.Live;

/// <summary>
/// How many live streams this process will hold open at once.
///
/// The static page costs a request. A live stream costs a held connection and a Razor render every
/// time a pass lands, for as long as the tab is open, and it is opened by anyone at all — this is
/// the first surface in the project without a login in front of it. So there is a ceiling, and when
/// it is reached the answer is a sentence rather than a slow site: the stream opens, says the feed
/// is full, and closes, and the page carries on doing what it does without one.
///
/// <b>The number is not a load calculation and does not pretend to be.</b> The renders are cheap and
/// they are already shared — every stream on the same pair reads through <see cref="Data.ArenaCache"/>,
/// so a hundred tabs on BTC/USD are one query. What the ceiling buys is a refusal we wrote against a
/// failure we did not: without it, the way this surface tells us it is overloaded is by becoming
/// slow for the people reading it. A stated limit is the same principle as a dash instead of a zero.
///
/// It is per process, and the real limit in front of it is the proxy's connection cap; if that one
/// is ever lower than this one, this class stops being reached and the page still behaves.
/// </summary>
public sealed class LiveStreamGate
{
    /// <summary>
    /// One hundred simultaneous watchers, on a site whose readership is measured in people rather
    /// than in thousands. Chosen so that the ceiling is reached by something going wrong — a script
    /// opening tabs, a proxy that never closes anything — long before it is reached by an audience.
    /// </summary>
    public const int MaxStreams = 100;

    private readonly int _max;
    private int _open;

    public LiveStreamGate() : this(MaxStreams)
    {
    }

    /// <summary>The ceiling is injectable for the tests; nothing configures it at runtime, because a
    /// limit that can be raised from a config file is a limit that gets raised instead of read.</summary>
    public LiveStreamGate(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        _max = max;
    }

    public int Open => Volatile.Read(ref _open);

    /// <summary>Takes a slot, or refuses. A compare-and-swap rather than an increment-then-check:
    /// the second shape lets a burst of arrivals push the count past the ceiling and then hand it
    /// back, which is a limit that is briefly not one.</summary>
    public bool TryEnter()
    {
        while (true)
        {
            var open = Volatile.Read(ref _open);
            if (open >= _max)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _open, open + 1, open) == open)
            {
                return true;
            }
        }
    }

    /// <summary>Gives a slot back. Called from a finally — a stream that ended because the reader
    /// closed the laptop lid must not leave its slot spent for the life of the process.</summary>
    public void Exit() => Interlocked.Decrement(ref _open);
}

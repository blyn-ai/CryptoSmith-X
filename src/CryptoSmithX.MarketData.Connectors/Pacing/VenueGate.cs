namespace CryptoSmithX.MarketData.Connectors.Pacing;

/// <summary>
/// The request ceiling of one venue — the thing the 0019 hierarchy was introduced for. A venue's
/// per-IP budget is shared by every segment, every collector and every background feed that talks to
/// it, so the ceiling is keyed on <c>exchange.code</c> ("weex"), never on a segment code
/// ("weex-futures"): two segments of one venue must contend for one budget, not two.
///
/// It does two things at once, and they are different guarantees:
///
///   * a RATE ceiling — starts are spaced at least <c>1/requestsPerSecond</c> apart, venue-wide;
///   * a CONCURRENCY ceiling — at most N requests may be in flight at once.
///
/// The second one is why this class exists at all. Before it, DepthCollector walked instruments one
/// at a time and paid the venue's network latency once per symbol: on production WEEX a 1005-symbol
/// sweep took 361 s against a 60 s interval, with the host idle. The requests were never the problem;
/// the serialisation was. Letting N of them overlap while keeping the same rate ceiling turns that
/// sweep into roughly (symbols / requestsPerSecond) seconds and asks the venue for nothing more per
/// second than the old code intended to.
///
/// This gate must not be nested: a caller holding a lease that waits for a second lease can deadlock
/// against the concurrency semaphore. One lease per outbound request, released before the next.
/// </summary>
public sealed class VenueGate
{
    /// <summary>
    /// How long a 429 parks the venue when the caller has nothing better. WEEX documents rate
    /// limiting by IP (not by key) and a 10 s IP ban for continued violation
    /// (https://www.weex.com/api-doc/contract/QuickStart/AccessRestrictions, echoed in the
    /// <c>rateLimits</c> field of GET /capi/v3/market/exchangeInfo); the other venues we speak to
    /// document no cooldown at all, so this is OUR conservative choice for them, not a vendor
    /// number. Callers that can read a Retry-After should pass it instead.
    /// </summary>
    public static readonly TimeSpan DefaultPenalty = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _minInterval;

    /// <summary>The instant the next request may START. Every claim moves it forward; nothing else
    /// decides when a caller runs. Guarded by <see cref="_sync"/>.</summary>
    private DateTimeOffset _nextStart = DateTimeOffset.MinValue;

    private long _penaltyUntilTicks;

    public VenueGate(string venueCode, int requestsPerSecond, int maxConcurrentRequests, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentRequests, 1);

        VenueCode = venueCode;
        RequestsPerSecond = requestsPerSecond;
        MaxConcurrentRequests = maxConcurrentRequests;
        _clock = clock;
        _minInterval = TimeSpan.FromSeconds(1.0 / requestsPerSecond);
        _slots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
    }

    public string VenueCode { get; }

    public int RequestsPerSecond { get; }

    public int MaxConcurrentRequests { get; }

    /// <summary>The gap the rate ceiling puts between two consecutive starts.</summary>
    public TimeSpan MinInterval => _minInterval;

    /// <summary>When the venue's last 429 stops holding us back, for reporting only — the wait itself
    /// is derived from <see cref="_nextStart"/>, see <see cref="Penalize"/>. default when never hit.</summary>
    public DateTimeOffset PenaltyUntil => new(Interlocked.Read(ref _penaltyUntilTicks), TimeSpan.Zero);

    /// <summary>
    /// Waits for a concurrency slot and for this caller's paced turn, then returns the lease that
    /// holds the slot. Dispose it as soon as the request finishes — a lease held across a second
    /// acquire is the one way to deadlock this class.
    /// </summary>
    public async ValueTask<VenueLease> AcquireAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTimeOffset start;
            lock (_sync)
            {
                // Claim the turn BEFORE waiting for it. The obvious alternative — read the schedule,
                // sleep, then run — hands the same instant to every caller that read it, which is a
                // stampede exactly where we were trying to be gentle. Claiming first makes the queue
                // resolve into a staircase whatever order the waiters wake up in.
                var now = _clock.GetUtcNow();
                start = _nextStart > now ? _nextStart : now;
                _nextStart = start + _minInterval;
            }

            var wait = start - _clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            }

            return new VenueLease(_slots);
        }
        catch
        {
            // Cancelled while waiting for our turn: the slot must go back, or the venue loses
            // capacity permanently every time a collector is stopped. The claimed turn is NOT
            // given back — a gap in the schedule is harmless, a double-booked instant is not.
            _slots.Release();
            throw;
        }
    }

    /// <summary>
    /// Tells the gate the venue pushed us away (HTTP 429). The cooldown is folded into the same
    /// schedule every other caller reads, rather than kept as a separate "am I penalised?" branch:
    /// a second branch is what let the first draft of this class wait out a penalty and then release
    /// every queued caller at the same millisecond. With one schedule that cannot be expressed —
    /// the first caller after a penalty starts when it ends, the second one interval later.
    /// </summary>
    public void Penalize(TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return;
        }

        lock (_sync)
        {
            var until = _clock.GetUtcNow() + cooldown;
            if (until > _nextStart)
            {
                _nextStart = until;

                // Inside the same guard as _nextStart, not after it: a second, shorter Penalize
                // arriving while a longer cooldown is still running must leave BOTH fields alone.
                // This used to run unconditionally below the guard, so a 60 s Retry-After followed
                // by a headerless 10 s penalty left the schedule correctly at +60 s while
                // PenaltyUntil — read straight off _penaltyUntilTicks, with no guard of its own —
                // was overwritten to +10 s. The reported number and the actual schedule must move
                // together or the console tells an operator the venue is clear 50 s before it is.
                Interlocked.Exchange(ref _penaltyUntilTicks, until.UtcTicks);
            }
        }
    }

    /// <summary>The <see cref="DefaultPenalty"/> cooldown, for callers with no Retry-After to go on.</summary>
    public void Penalize() => Penalize(DefaultPenalty);
}

/// <summary>One in-flight request's claim on a venue's concurrency budget. Releasing twice would hand
/// the venue capacity it never had, so the release is idempotent.</summary>
public sealed class VenueLease : IDisposable
{
    private SemaphoreSlim? _slots;

    internal VenueLease(SemaphoreSlim slots) => _slots = slots;

    public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
}

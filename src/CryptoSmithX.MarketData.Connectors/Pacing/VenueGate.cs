namespace CryptoSmithX.MarketData.Connectors.Pacing;

/// <summary>
/// The request ceiling of one venue — the thing the 0019 hierarchy was introduced for. A venue's
/// per-IP budget is shared by every segment, every collector and every background feed that talks to
/// it, so the ceiling is keyed on <c>exchange.code</c> ("weex"), never on a segment code
/// ("weex-futures"): two segments of one venue must contend for one budget, not two.
///
/// It does two things at once, and they are different guarantees:
///
///   * a RATE ceiling — a TOKEN BUCKET of <see cref="MaxConcurrentRequests"/> tokens, refilling at
///     <see cref="RequestsPerSecond"/>, venue-wide;
///   * a CONCURRENCY ceiling — at most N requests may be in flight at once (the same N as the
///     bucket's own size — see BURST below for why one number serves both).
///
/// BURST, AND WHY THE BUCKET REPLACED A LADDER. This class used to space every start at least
/// <c>1/requestsPerSecond</c> apart from the one before it, unconditionally — a round of 32 parallel
/// callers came out as a staircase, one every <c>1/rps</c>, so a 25-symbol sweep had a measured FLOOR
/// of ~0.8 s before a single byte of network latency, on venues whose own measurements (0036,
/// plans/collection-policy.md §5) showed they accept a round of 32+ requests landing at once and rate-
/// limit by a WINDOW, not by evenness. The bucket starts full and a burst that fits in it is granted
/// together — the whole point of a "32 parallel, no self-inflicted delay" round. Only once the bucket
/// is spent does the schedule fall back to the old per-request spacing, at exactly the same
/// <c>1/requestsPerSecond</c> the ladder used. Burst size is <see cref="MaxConcurrentRequests"/>, not
/// a separate number: a burst wider than the concurrency ceiling could not run at once anyway, so a
/// second field would only ever equal the first or be a lie.
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
    private readonly double _rps;
    private readonly int _burst;

    /// <summary>Tokens currently in the bucket, as a continuous quantity rather than an integer count
    /// — a claim that cannot be paid in full goes NEGATIVE rather than blocking on a discrete
    /// counter, and the caller's wait is computed straight from the deficit. This is what lets the
    /// claim happen before the wait, the same ordering <see cref="AcquireAsync"/>'s predecessor used
    /// for the identical reason: claiming first turns a queue of waiters into a staircase whatever
    /// order they happen to wake in, instead of a stampede at whichever instant one of them notices a
    /// token is free. Guarded by <see cref="_sync"/>; refilled lazily, in <see cref="RefillLocked"/>,
    /// rather than on a timer nobody would be waiting on anyway.</summary>
    private double _availableTokens;

    private DateTimeOffset _lastRefill;

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
        _rps = requestsPerSecond;
        _burst = maxConcurrentRequests;
        _slots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        _lastRefill = clock.GetUtcNow();
        _availableTokens = _burst;
    }

    public string VenueCode { get; }

    public int RequestsPerSecond { get; }

    public int MaxConcurrentRequests { get; }

    /// <summary>When the venue's last 429 stops holding us back, for reporting only — the wait itself
    /// is derived from <see cref="_availableTokens"/>, see <see cref="Penalize"/>. default when never
    /// hit.</summary>
    public DateTimeOffset PenaltyUntil => new(Interlocked.Read(ref _penaltyUntilTicks), TimeSpan.Zero);

    /// <summary>
    /// Waits for a concurrency slot and for this caller's turn at the bucket, then returns the lease
    /// that holds the slot. Dispose it as soon as the request finishes — a lease held across a second
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
                var now = _clock.GetUtcNow();
                RefillLocked(now);

                // Claim the token BEFORE waiting for it, even into deficit — see the field doc on
                // _availableTokens for why. A claim that lands while the bucket is at or above zero
                // pays in full and starts now; one that does not is owed the difference, paid back at
                // _rps, and that deficit alone is this caller's wait.
                _availableTokens -= 1;
                start = _availableTokens >= 0
                    ? now
                    : now + TimeSpan.FromSeconds(-_availableTokens / _rps);
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
            // capacity permanently every time a collector is stopped. The claimed token is NOT
            // refunded — a bucket that ends up a little emptier than it strictly needed to be is
            // harmless; crediting it back would let a burst of cancellations refill capacity nobody
            // is about to spend responsibly.
            _slots.Release();
            throw;
        }
    }

    /// <summary>Brings <see cref="_availableTokens"/> up to date for <paramref name="now"/>, capped at
    /// <see cref="_burst"/> — a bucket that accrued for an hour of nobody calling must not hand out an
    /// hour's worth of requests the instant someone does. Caller holds <see cref="_sync"/>.</summary>
    private void RefillLocked(DateTimeOffset now)
    {
        var elapsed = (now - _lastRefill).TotalSeconds;
        _lastRefill = now;
        if (elapsed <= 0)
        {
            return;
        }

        _availableTokens = Math.Min(_burst, _availableTokens + (elapsed * _rps));
    }

    /// <summary>
    /// Tells the gate the venue pushed us away (HTTP 429). The cooldown is folded into the same
    /// bucket every other caller reads, rather than kept as a separate "am I penalised?" branch: a
    /// second branch is what let the first draft of this class wait out a penalty and then release
    /// every queued caller at the same millisecond. Expressed as a token DEFICIT deep enough that the
    /// next claim's wait is exactly <paramref name="cooldown"/>, so the queue resolves into the same
    /// staircase a burst draining the bucket naturally would — the caller right after the penalty
    /// waits the full cooldown, the one after that waits cooldown + 1/rps, and so on.
    /// </summary>
    public void Penalize(TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return;
        }

        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            RefillLocked(now);

            // The deficit that makes ONE claim's wait equal exactly `cooldown`: claiming subtracts
            // one more token, and a wait of `cooldown` needs the post-claim balance to be
            // `-cooldown * _rps`. Solving for the PRE-claim balance this call must leave behind:
            // floor = 1 - cooldown * rps.
            var floor = 1 - (cooldown.TotalSeconds * _rps);

            // Only ever MORE restrictive than what is already scheduled — a second, shorter penalty
            // arriving while a longer one is still running must leave the schedule alone. This used
            // to be an unconditional overwrite in the ladder version and the same bug shape applies
            // here: a 60 s Retry-After followed by a headerless 10 s penalty must still clear at
            // +60 s, not snap back to +10 s.
            if (floor < _availableTokens)
            {
                _availableTokens = floor;

                // Inside the same guard, not after it — see the ladder-era comment this one replaces:
                // the reported number and the actual schedule must move together or the console tells
                // an operator the venue is clear before it is.
                Interlocked.Exchange(ref _penaltyUntilTicks, (now + cooldown).UtcTicks);
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

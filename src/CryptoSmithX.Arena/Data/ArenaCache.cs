using System.Collections.Concurrent;

namespace CryptoSmithX.Arena.Data;

/// <summary>
/// A one-second, single-flight cache in front of the public queries.
///
/// It exists for one shape of traffic: a page that anyone can open, refreshed by many people at
/// once, all asking for the same handful of pairs. Without it, N simultaneous visitors are N
/// identical scans against a role whose connection ceiling is 30 (0025).
///
/// Four properties, and each one is a decision:
///
/// <b>Failures are not cached.</b> A cached error on a public page lives exactly as long as nobody
/// is looking at it — the moment traffic arrives to re-run the query, the cache is answering with
/// the failure instead. So a faulted load is evicted and the next caller tries again.
///
/// <b>The shared load does not carry a caller's cancellation token.</b> One visitor closing the tab
/// would otherwise cancel a query that a dozen other requests are waiting on. The work runs
/// uncancelled; each caller waits on it with their own token and gives up alone.
///
/// <b>The table is bounded, and that is a safety property rather than a tuning knob.</b> This cache
/// sits on a surface with no authentication, so its keys are written by anonymous callers at
/// whatever rate they choose (<c>?q=</c> goes into the key). An unbounded dictionary of rendered
/// results is then a memory leak an outsider drives: distinct keys accumulate for the life of the
/// process, and 30,000 requests with distinct terms take it past two gigabytes. So there is a
/// ceiling on how many answers are remembered and a ceiling on how long a key may be, and past
/// either one the load still runs — it is simply not remembered. Losing the cache degrades to the
/// site without a cache; losing the bound ends the process. See <see cref="MaxEntries"/>.
///
/// <b>The payload holds no clock.</b> "Now" is the time of the REQUEST, never the time the cache
/// filled. That is not enforced here — it is enforced by the cached records having no field to put
/// it in (see <c>PairComparison</c>) — but it is the reason a second-old answer is still honest:
/// the instants inside it are absolute, so the age computed against the request time is true
/// regardless of when the row was fetched. Blueprint §5.
/// </summary>
public sealed class ArenaCache
{
    /// <summary>
    /// About a second. Chosen against the fastest call on the surface — the snapshot ticker at
    /// 10 s (0014) — so at worst a visitor sees a figure a tenth of one interval behind the row in
    /// the database, and the age printed beside it says so anyway, because the age is computed from
    /// the absolute instant rather than from the cache.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many answers the process will remember at once.
    ///
    /// The number is chosen against what an entry costs and what the surface actually asks for, in
    /// that order. An entry holds one rendered result: a pair page's rows and their candles, or a
    /// list of at most <see cref="ArenaStore.MaxPairs"/> cards. Two hundred and fifty-six of those
    /// is tens of megabytes at the worst end, which a container can hold; no number of them is,
    /// which is the state this constant exists to end. On the other side, the traffic this cache was
    /// built for — many people opening the same handful of pairs within the same second — needs
    /// nowhere near that many keys, so the ceiling is never reached by the readers it serves.
    ///
    /// <b>An entry is evicted to make room only when it is dead, and then the coldest one first.</b>
    /// Dead means finished AND past the window: <see cref="TryHit{T}"/> can never answer from it
    /// again, so it is holding a slot and nothing else. When the sweep frees nothing the ARRIVING
    /// key is the one refused, and exactly one entry is taken per arrival rather than every dead
    /// entry at once.
    ///
    /// THIS PARAGRAPH USED TO SAY entries already in the table are never evicted at all, and the
    /// sweep below made that false the day it was added — a hot key goes dead one second after it
    /// was last renewed, and the old sweep took every dead entry it found, so the pair page a
    /// hundred visitors were sharing was swept the instant it turned one second old and the flood's
    /// next arrival took the slot before their next request did. Measured: 3,000 shared GETs of one
    /// pair page ran 7 queries alone and 141 under a flood of distinct <c>?q=</c> terms, against a
    /// role whose connection ceiling is 30. The single-flight collapse this class exists to provide
    /// was lost for everyone, by the mechanism written here to protect it.
    ///
    /// What replaces it is a rule about WHICH entry, in two parts, and the first part is the one that
    /// actually holds:
    ///
    /// <b>An entry somebody JOINED keeps its slot for one further window after it dies</b> — see
    /// <see cref="JoinedGrace"/>. <see cref="Entry.Joined"/> is set when a second caller is served
    /// the answer a first caller started, which is the entire subject of this class and is exactly
    /// what a flood's keys never have: each distinct term is written once, asked for once, never
    /// shared. A page real traffic is sharing is re-admitted under its own name the moment the next
    /// visitor arrives, so a whole extra window is more than it needs; without the grace, the sweep
    /// and that visitor were in a race measured in fractions of a millisecond, and at 2,638 arriving
    /// keys a second the sweep wins it often enough to cost twenty times the database work.
    ///
    /// <b>Then unjoined first, and oldest first.</b> The rest of the order, for when the grace is not
    /// what decides it. A key inserted once and abandoned only gets older; a shared page is renewed
    /// every window and is therefore the youngest dead entry on the table.
    ///
    /// The honest limit: a flood that sends each of its terms TWICE inside one second buys its own
    /// keys the grace and the first sort key both, and then the tie falls to age, where a page real
    /// traffic renews every second still wins. It costs the attacker double the requests to reach a
    /// table that evicts its own keys before ours.
    ///
    /// The grace cannot ratchet the table shut, which is the thing to check about any rule that
    /// refuses to evict: it is a delay and not a reprieve, so a joined entry nobody comes back for is
    /// swept one window later than an unjoined one, and the worst case is that new keys go uncached
    /// for one extra second.
    ///
    /// Rejected: no sweep at all, which is what the old paragraph described and would have made it
    /// true. One flood then fills the table with 256 dead keys FOREVER, and no pair page anybody
    /// opens afterwards is ever cached again for the life of the process. That is a permanent
    /// poisoning bought with 256 requests, and it is worse than the transient one it removes.
    /// </summary>
    public const int MaxEntries = 256;

    /// <summary>
    /// How much longer than its window a JOINED entry keeps its slot once it is dead.
    ///
    /// One further window, and the number is not free: it has to be at least as long as the gap
    /// between two requests for a page people are actually sharing, and a page nobody asks for
    /// within a second of its expiry is not the page this cache exists for. Longer buys nothing —
    /// the entry is re-admitted under its own key the instant a visitor returns, which resets the
    /// clock — and costs the table a slot for as long as it lasts.
    /// </summary>
    public static readonly TimeSpan JoinedGrace = Ttl;

    /// <summary>
    /// The longest string that may become a key.
    ///
    /// The keys this application builds are short by construction — <c>"pair:" + two family codes</c>
    /// bounded to sixteen characters each by <see cref="PairAddress"/>, or <c>"pairs:" + a search
    /// term</c>. The search term is the one an anonymous caller writes, and Kestrel will carry
    /// roughly eight kilobytes of it in the request line. A term longer than this is answered
    /// truthfully — it is passed to the query exactly as it was typed, never trimmed to fit, because
    /// a filter that silently searches for something shorter than what was asked is the same lie as
    /// a zero standing in for a dash — but it is not remembered.
    /// </summary>
    public const int MaxKeyLength = 64;

    // Assigned through the constructor. A `private readonly TimeProvider _clock;` with no
    // constructor is CS8618, and CS8618 under TreatWarningsAsErrors is a build failure, not a
    // squiggle.
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>How many answers are held right now. Read by the tests that hold this class to its
    /// ceiling, and the one number worth exporting if this surface ever gets metrics.</summary>
    public int Count => _entries.Count;

    public ArenaCache(TimeProvider clock) => _clock = clock;

    private sealed class Entry
    {
        public object? Work;
        public DateTimeOffset StartedAt;

        /// <summary>Whether anyone other than the caller who started this work was served from it.
        /// It is the single-flight collapse, observed: a key one visitor asked for once and nobody
        /// joined bought this table nothing, and it is the first thing given up when the table is
        /// full. Written from several threads and read from one under <c>_gate</c>; a bool
        /// assignment is atomic and a missed set only costs the entry its place in the queue for
        /// one sweep.</summary>
        public bool Joined;
    }

    /// <summary>
    /// Returns the shared in-flight or freshly cached result for <paramref name="key"/>, starting
    /// the work only if nobody else has.
    /// </summary>
    /// <remarks>
    /// A key the cache refuses to hold — too long, or arriving at a full table — is not an error and
    /// not an empty answer: the load runs and its result is returned uncached. The one thing that
    /// changes for such a caller is that it carries THEIR cancellation token, because nobody else is
    /// waiting on it, so a visitor who closes the tab takes their own query with them.
    /// </remarks>
    public Task<T> GetAsync<T>(string key, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        if (key.Length > MaxKeyLength)
        {
            return load(ct);
        }

        if (TryHit<T>(key, out var hit))
        {
            return hit.WaitAsync(ct);
        }

        Task<T> shared;
        lock (_gate)
        {
            // Re-checked inside the lock: two visitors arriving in the same millisecond must join
            // one query, not start two. This is the whole of "single-flight".
            if (TryHit<T>(key, out hit))
            {
                return hit.WaitAsync(ct);
            }

            if (!HasRoomFor(key))
            {
                return load(ct);
            }

            var entry = new Entry { StartedAt = _clock.GetUtcNow() };
            shared = RunAsync(key, entry, load);
            entry.Work = shared;
            _entries[key] = entry;
        }

        return shared.WaitAsync(ct);
    }

    /// <summary>
    /// Whether this key may be admitted. Called under <c>_gate</c>, which is what makes the count
    /// checked here the count the insert below will produce.
    /// </summary>
    private bool HasRoomFor(string key)
    {
        // Replacing an expired entry under a key already present adds nothing, so it is always
        // allowed: refusing it would leave the table holding a dead answer for a live reader.
        if (_entries.ContainsKey(key) || _entries.Count < MaxEntries)
        {
            return true;
        }

        // Swept only at the ceiling, and only entries that are finished AND past the window. A
        // running entry is never dead however old it looks — visitors are waiting on it. A timer
        // sweeping in the background was rejected: with a one-second window the table turns over
        // constantly on its own, and a thread waking every second to walk a dictionary that is
        // usually a dozen entries long is a cost paid on every deployment to fix a state that only
        // exists while somebody is attacking this one.
        //
        // ONE ENTRY PER ARRIVAL, COLDEST FIRST, and the order is the whole defence — see the
        // paragraph on MaxEntries. Taking every dead entry at once is what let a flood clear the
        // shared pair page out of the table a second after it was last renewed.
        var now = _clock.GetUtcNow();
        var dead = _entries
            .Where(e => Sweepable(e.Value, now))
            .OrderBy(e => e.Value.Joined ? 1 : 0)
            .ThenBy(e => e.Value.StartedAt)
            .ToList();

        foreach (var entry in dead)
        {
            if (_entries.Count < MaxEntries)
            {
                break;
            }

            _entries.TryRemove(entry);
        }

        return _entries.Count < MaxEntries;
    }

    /// <summary>
    /// Whether this entry may be taken to make room.
    ///
    /// Finished AND past its window, so <see cref="TryHit{T}"/> can never answer from it again — a
    /// RUNNING entry is never sweepable however old it looks, because visitors are waiting on it —
    /// and, if a second caller ever joined it, past <see cref="JoinedGrace"/> on top of that.
    /// </summary>
    private static bool Sweepable(Entry entry, DateTimeOffset now) =>
        entry.Work is Task { IsCompleted: true }
        && now - entry.StartedAt >= (entry.Joined ? Ttl + JoinedGrace : Ttl);

    private bool TryHit<T>(string key, out Task<T> task)
    {
        task = null!;
        if (!_entries.TryGetValue(key, out var entry) || entry.Work is not Task<T> existing)
        {
            return false;
        }

        // A finished-and-faulted entry is never a hit; RunAsync evicts it, and this guards the
        // window before that eviction lands. Still-running entries always are, however old — that
        // is the single flight, and joining it is cheaper than a second identical query. A query
        // that hangs therefore holds every arriving visitor, and what bounds that is Npgsql's own
        // command timeout: the load fails, the entry is evicted, and the next visitor tries again.
        // A timeout of our own here would be a second deadline to keep in agreement with that one.
        if (existing.IsCompleted
            && (existing.IsFaulted || existing.IsCanceled || _clock.GetUtcNow() - entry.StartedAt >= Ttl))
        {
            return false;
        }

        // A second caller was served from work a first caller started, which is the single flight
        // this class is for. Recorded on the entry, because it is what decides whether the entry is
        // worth a slot when the table is full and a flood of unshared keys is arriving.
        entry.Joined = true;
        task = existing;
        return true;
    }

    private async Task<T> RunAsync<T>(string key, Entry entry, Func<CancellationToken, Task<T>> load)
    {
        try
        {
            return await load(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Removed by identity, not by key: a later entry for the same key belongs to a caller
            // who has already started over, and evicting theirs would be this failure taking a
            // healthy query down with it.
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }
    }
}

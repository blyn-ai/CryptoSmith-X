using CryptoSmithX.Studio.Data;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.Studio.Tests;

/// <summary>
/// The cache in front of the public queries. Three behaviours, and each one is a decision that would
/// be invisible in production until it mattered.
/// </summary>
public sealed class StudioCacheTests
{
    [Fact]
    public async Task Two_visitors_arriving_together_share_one_query()
    {
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);
        var gate = new TaskCompletionSource();
        var started = 0;

        Task<int> Load(CancellationToken _)
        {
            Interlocked.Increment(ref started);
            return gate.Task.ContinueWith(_ => 42, TaskScheduler.Default);
        }

        var a = cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None);
        var b = cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None);
        gate.SetResult();

        Assert.Equal(42, await a);
        Assert.Equal(42, await b);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task A_second_visitor_inside_the_window_is_served_the_same_answer()
    {
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);
        var calls = 0;

        Task<int> Load(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        Assert.Equal(1, await cache.GetAsync("k", Load, CancellationToken.None));
        clock.Advance(StudioCache.Ttl - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, await cache.GetAsync("k", Load, CancellationToken.None));
    }

    [Fact]
    public async Task Past_the_window_the_query_runs_again()
    {
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);
        var calls = 0;

        Task<int> Load(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        Assert.Equal(1, await cache.GetAsync("k", Load, CancellationToken.None));
        clock.Advance(StudioCache.Ttl);
        Assert.Equal(2, await cache.GetAsync("k", Load, CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_is_not_cached()
    {
        // A cached error on a public page lives exactly as long as nobody is looking at it: the
        // moment traffic arrives that would have re-run the query, the cache answers with the
        // failure instead.
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);
        var calls = 0;

        Task<int> Load(CancellationToken _) =>
            ++calls == 1
                ? Task.FromException<int>(new InvalidOperationException("connection refused"))
                : Task.FromResult(7);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync("k", Load, CancellationToken.None));

        // No clock advance: the very next request, in the same millisecond, gets a real attempt.
        Assert.Equal(7, await cache.GetAsync("k", Load, CancellationToken.None));
    }

    [Fact]
    public async Task One_visitor_giving_up_does_not_cancel_the_query_the_others_are_waiting_on()
    {
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);
        var gate = new TaskCompletionSource();
        var sawCancellation = false;

        Task<int> Load(CancellationToken ct)
        {
            sawCancellation = ct.CanBeCanceled;
            return gate.Task.ContinueWith(_ => 5, TaskScheduler.Default);
        }

        using var leaving = new CancellationTokenSource();
        var abandoned = cache.GetAsync("k", Load, leaving.Token);
        var staying = cache.GetAsync("k", Load, CancellationToken.None);

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        gate.SetResult();
        Assert.Equal(5, await staying);
        Assert.False(sawCancellation, "the shared load must not carry any one caller's token");
    }

    // ── The ceiling, and what it is allowed to take ─────────────────────────────────────────────

    [Fact]
    public async Task A_flood_of_unshared_keys_cannot_take_the_page_everyone_is_sharing()
    {
        // The failure this ordering exists to close. The sweep took EVERY dead entry it found, and a
        // hot key is dead one second after it was last renewed — so the pair page a hundred visitors
        // were sharing was swept the instant it turned one second old, the flood's next arrival took
        // the freed slot, and the visitors' next request found a full table and no key of theirs in
        // it. Measured on the running container: 7 queries for 3,000 shared GETs alone, 141 for the
        // same 3,000 under a flood.
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);
        var loads = 0;

        Task<int> Load(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref loads));

        // The shared page: two visitors, so the second one JOINS the first one's work. That is the
        // whole of what this cache does, and it is what the flood's keys never have.
        await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None);
        await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None);
        Assert.Equal(1, loads);

        for (var i = 0; i < StudioCache.MaxEntries - 1; i++)
        {
            await cache.GetAsync("pairs:seed" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        Assert.Equal(StudioCache.MaxEntries, cache.Count);

        // A second passes: everything on the table is now dead, the shared page included.
        clock.Advance(StudioCache.Ttl);

        // …and the flood arrives, one distinct term at a time. More of them than the table has
        // slots, so it is refilled to the ceiling and every slot in it has been contested.
        for (var i = 0; i < StudioCache.MaxEntries * 2; i++)
        {
            await cache.GetAsync("pairs:flood" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        Assert.Equal(StudioCache.MaxEntries, cache.Count);

        // The visitors come back. Two of them, in the same millisecond: if their key is still in the
        // table it is re-admitted under its own name and they share one query, which is the whole
        // point. If it was swept, the table is full of the flood's keys and they are refused — two
        // queries for two visitors, and twenty times the database work at real concurrency.
        var before = loads;
        var gate = new TaskCompletionSource();

        Task<int> Slow(CancellationToken _)
        {
            Interlocked.Increment(ref loads);
            return gate.Task.ContinueWith(_ => 1, TaskScheduler.Default);
        }

        var a = cache.GetAsync("pair:BTC/USD", Slow, CancellationToken.None);
        var b = cache.GetAsync("pair:BTC/USD", Slow, CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(before + 1, loads);
    }

    [Fact]
    public async Task The_grace_is_a_delay_and_not_a_reprieve()
    {
        // The check to make about any rule that refuses to evict: a joined entry nobody ever comes
        // back for must not hold its slot for the life of the process, or one popular page during a
        // deploy would ratchet the table shut behind it.
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);

        await cache.GetAsync("pair:BTC/USD", _ => Task.FromResult(1), CancellationToken.None);
        await cache.GetAsync("pair:BTC/USD", _ => Task.FromResult(1), CancellationToken.None);

        for (var i = 0; i < StudioCache.MaxEntries - 1; i++)
        {
            await cache.GetAsync("pairs:seed" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        // Past its window AND past the grace, and nobody has asked for it since.
        clock.Advance(StudioCache.Ttl + StudioCache.JoinedGrace);

        for (var i = 0; i < StudioCache.MaxEntries * 2; i++)
        {
            await cache.GetAsync("pairs:later" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        var loads = 0;
        var gate = new TaskCompletionSource();

        Task<int> Slow(CancellationToken _)
        {
            Interlocked.Increment(ref loads);
            return gate.Task.ContinueWith(_ => 1, TaskScheduler.Default);
        }

        var a = cache.GetAsync("pair:BTC/USD", Slow, CancellationToken.None);
        var b = cache.GetAsync("pair:BTC/USD", Slow, CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(2, loads); // its slot was given up, and the two callers ran uncached
    }

    [Fact]
    public async Task Exactly_one_entry_is_taken_per_arriving_key()
    {
        // The old sweep emptied the table of every dead entry at once, so one arriving key cost 255
        // others their slot and the flood's next 255 arrivals were admitted free. One per arrival
        // is what makes the ordering above mean anything.
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);

        for (var i = 0; i < StudioCache.MaxEntries; i++)
        {
            await cache.GetAsync("k" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        clock.Advance(StudioCache.Ttl);
        await cache.GetAsync("new", _ => Task.FromResult(0), CancellationToken.None);

        Assert.Equal(StudioCache.MaxEntries, cache.Count);
    }

    [Fact]
    public async Task A_live_entry_is_never_taken_and_the_arriving_key_is_the_one_refused()
    {
        // Nothing dead, so nothing to sweep, and the answer is to run the load uncached rather than
        // to evict an answer somebody may be about to be served from.
        var clock = new FakeTimeProvider();
        var cache = new StudioCache(clock);

        for (var i = 0; i < StudioCache.MaxEntries; i++)
        {
            await cache.GetAsync("k" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        var loads = 0;
        Task<int> Load(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref loads));

        // No clock advance: every entry is inside its window.
        Assert.Equal(1, await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None));
        Assert.Equal(StudioCache.MaxEntries, cache.Count);
        Assert.Equal(2, await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None));

        // And the entries that were there are all still there.
        Assert.Equal(0, await cache.GetAsync("k0", _ => Task.FromResult(-1), CancellationToken.None));
    }

    [Fact]
    public void The_cached_payload_has_nowhere_to_put_a_clock()
    {
        // Not a style check. "now" in a cached response is the time of the REQUEST, and the way that
        // is guaranteed is that the record which gets cached cannot carry a timestamp of its own —
        // the instants inside it are absolute, so a row served from a 900 ms-old cache still reports
        // a truthful age.
        var names = typeof(Models.PairComparison).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Now", names);
        Assert.DoesNotContain("AsOf", names);
        Assert.DoesNotContain("BuiltAt", names);
    }
}

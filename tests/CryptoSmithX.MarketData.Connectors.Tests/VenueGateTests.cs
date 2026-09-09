using CryptoSmithX.MarketData.Connectors.Pacing;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The venue request ceiling. Everything here runs on a <see cref="FakeTimeProvider"/>: the gate
/// takes its clock from outside for exactly this reason, so the schedule can be asserted instant by
/// instant instead of slept through.
/// </summary>
public sealed class VenueGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The whole reason the ladder became a bucket (plans/prompt-rest-hygiene.md): a round of 32
    /// parallel callers used to come out spaced 1/rps apart regardless of how many the venue could
    /// actually take at once, so a 25-symbol sweep had a measured floor of ~0.8 s before a single
    /// byte of network latency. Burst size is <see cref="VenueGate.MaxConcurrentRequests"/>, so a
    /// round that fits inside the concurrency ceiling is granted TOGETHER — no clock advance at all
    /// between the first caller and the last.
    /// </summary>
    [Fact]
    public async Task A_burst_up_to_the_ceiling_is_granted_together_not_as_a_ladder()
    {
        var clock = new FakeTimeProvider(T0);
        var gate = new VenueGate("weex", requestsPerSecond: 10, maxConcurrentRequests: 32, clock);

        var leases = Enumerable.Range(0, 32)
            .Select(_ => gate.AcquireAsync(CancellationToken.None).AsTask())
            .ToArray();

        foreach (var lease in leases)
        {
            Assert.True(await Granted(lease), "a burst-sized round must be granted together, not spaced");
        }

        foreach (var lease in leases)
        {
            (await lease).Dispose();
        }
    }

    /// <summary>Only once the bucket is spent does pacing reappear, and at exactly the rate the old
    /// ladder used throughout — 1/rps between one grant and the next.</summary>
    [Fact]
    public async Task The_request_after_a_spent_burst_waits_exactly_one_interval()
    {
        var clock = new FakeTimeProvider(T0);
        var gate = new VenueGate("weex", requestsPerSecond: 10, maxConcurrentRequests: 4, clock);

        var burst = new List<VenueLease>();
        for (var i = 0; i < 4; i++)
        {
            burst.Add(await gate.AcquireAsync(CancellationToken.None));
        }

        // Concurrency slots go back; the bucket itself does not refund on an ordinary dispose.
        foreach (var lease in burst)
        {
            lease.Dispose();
        }

        var next = gate.AcquireAsync(CancellationToken.None).AsTask();
        Assert.True(await StillWaiting(next), "the bucket is spent; 10 req/s owes one token in 100 ms");

        clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.True(await StillWaiting(next), "99 ms is not 100 ms");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(await Granted(next));
        (await next).Dispose();
    }

    /// <summary>A bucket left alone refills, and a second burst is granted together exactly as the
    /// first one was — this is what makes the round REPEATABLE, not a one-off head start.</summary>
    [Fact]
    public async Task Tokens_refill_between_bursts_and_a_second_burst_is_also_granted_together()
    {
        var clock = new FakeTimeProvider(T0);
        var gate = new VenueGate("weex", requestsPerSecond: 10, maxConcurrentRequests: 4, clock);

        var first = new List<VenueLease>();
        for (var i = 0; i < 4; i++)
        {
            first.Add(await gate.AcquireAsync(CancellationToken.None));
        }

        foreach (var lease in first)
        {
            lease.Dispose();
        }

        // The whole bucket back: 4 tokens at 10/s.
        clock.Advance(TimeSpan.FromMilliseconds(400));

        var second = Enumerable.Range(0, 4).Select(_ => gate.AcquireAsync(CancellationToken.None).AsTask()).ToArray();
        foreach (var lease in second)
        {
            Assert.True(await Granted(lease), "a fully-refilled bucket grants a second burst together too");
        }

        foreach (var lease in second)
        {
            (await lease).Dispose();
        }
    }

    /// <summary>
    /// The bug this class was rewritten to make impossible. A gate that waits out a 429 penalty and
    /// then hands a lease to whoever was queued releases all of them at the same instant — a
    /// stampede precisely where we were being careful. Claiming the turn before waiting for it turns
    /// the queue into a staircase: penalty end, +1 interval, +2 intervals.
    ///
    /// An implementation with a separate "am I penalised?" branch that returns without touching the
    /// schedule passes the first assertion below and fails the second.
    /// </summary>
    [Fact]
    public async Task A_penalty_staggers_the_queue_instead_of_releasing_it_at_once()
    {
        var clock = new FakeTimeProvider(T0);
        var gate = new VenueGate("weex", requestsPerSecond: 10, maxConcurrentRequests: 8, clock);

        gate.Penalize(TimeSpan.FromSeconds(1));
        Assert.Equal(T0 + TimeSpan.FromSeconds(1), gate.PenaltyUntil);

        var queued = new[]
        {
            gate.AcquireAsync(CancellationToken.None).AsTask(),
            gate.AcquireAsync(CancellationToken.None).AsTask(),
            gate.AcquireAsync(CancellationToken.None).AsTask(),
        };

        Assert.True(await StillWaiting(queued[0]), "nobody may start while the venue is holding us off");

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await Granted(queued[0]), "the penalty is over; one caller may go");
        Assert.True(
            await StillWaiting(queued[1]),
            "the whole queue restarting at the instant the penalty ends is the stampede we are avoiding");
        Assert.True(await StillWaiting(queued[2]));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(await Granted(queued[1]));
        Assert.True(await StillWaiting(queued[2]));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(await Granted(queued[2]));

        foreach (var lease in queued)
        {
            (await lease).Dispose();
        }
    }

    [Fact]
    public async Task A_penalty_never_pulls_the_schedule_backwards()
    {
        var clock = new FakeTimeProvider(T0);
        var gate = new VenueGate("weex", requestsPerSecond: 10, maxConcurrentRequests: 8, clock);

        gate.Penalize(TimeSpan.FromSeconds(10));
        // A second, shorter 429 arriving while the first cooldown is still running must not shorten
        // it: the venue said ten seconds, and the later message is not permission to go earlier.
        gate.Penalize(TimeSpan.FromMilliseconds(50));

        // PenaltyUntil is what an operator reads off the console, and it must never disagree with
        // the schedule it is reporting on. A bug here once let the shorter, later Penalize
        // overwrite PenaltyUntil down to T0+50ms while _nextStart correctly stayed at T0+10s — the
        // console would have said the venue was clear nearly ten seconds before it actually was.
        Assert.Equal(T0 + TimeSpan.FromSeconds(10), gate.PenaltyUntil);

        var caller = gate.AcquireAsync(CancellationToken.None).AsTask();
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.True(await StillWaiting(caller));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await Granted(caller));
        (await caller).Dispose();
    }

    [Fact]
    public async Task No_more_requests_are_in_flight_than_the_venue_allows()
    {
        var clock = new FakeTimeProvider(T0);

        // A rate ceiling this high, plus a clock already far past every claimed turn, makes pacing
        // a no-op here: what is under test is the concurrency ceiling alone.
        var gate = new VenueGate("weex", requestsPerSecond: 1000, maxConcurrentRequests: 2, clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        var first = await gate.AcquireAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await gate.AcquireAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(1));
        var third = gate.AcquireAsync(CancellationToken.None).AsTask();
        Assert.True(await StillWaiting(third), "two in flight is the whole budget");

        first.Dispose();
        Assert.True(await Granted(third), "a finished request hands its slot to the next caller");

        second.Dispose();
        (await third).Dispose();
    }

    /// <summary>
    /// Burst size is now the concurrency ceiling (see the class doc comment on VenueGate), so with a
    /// ceiling of 2 both a held lease and a second caller would fit in the initial burst and neither
    /// would ever wait — this test would pass for the wrong reason. Concurrency 1 forces the real
    /// scenario apart into its two separate waits: `held.Dispose()` frees the ONLY semaphore slot
    /// while leaving the bucket spent, so `abandoned` gets PAST `_slots.WaitAsync` immediately and is
    /// then stuck in the pacing delay — the exact branch whose catch block this test exists to pin.
    /// </summary>
    [Fact]
    public async Task A_cancelled_wait_gives_its_slot_back()
    {
        var clock = new FakeTimeProvider(T0);
        var gate = new VenueGate("weex", requestsPerSecond: 10, maxConcurrentRequests: 1, clock);

        var held = await gate.AcquireAsync(CancellationToken.None);
        held.Dispose(); // the one concurrency slot is free; the one token is not

        using var cts = new CancellationTokenSource();
        var abandoned = gate.AcquireAsync(cts.Token).AsTask();
        Assert.True(await StillWaiting(abandoned), "the slot is free but the bucket owes a refill");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        // A collector stopped mid-wait must not cost the venue its one slot forever: `abandoned` had
        // already passed the semaphore before being cancelled in the pacing delay, so this can only
        // be granted if the cancellation's catch block returned it.
        clock.Advance(TimeSpan.FromSeconds(10));
        var next = gate.AcquireAsync(CancellationToken.None).AsTask();
        Assert.True(await Granted(next));
        (await next).Dispose();
    }

    [Fact]
    public async Task Releasing_a_lease_twice_does_not_invent_capacity()
    {
        var clock = new FakeTimeProvider(T0);
        var gate = new VenueGate("weex", requestsPerSecond: 1000, maxConcurrentRequests: 1, clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        var lease = await gate.AcquireAsync(CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        clock.Advance(TimeSpan.FromSeconds(1));
        var a = await gate.AcquireAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(1));
        var b = gate.AcquireAsync(CancellationToken.None).AsTask();
        Assert.True(await StillWaiting(b), "a double release must not have widened the venue to two");

        a.Dispose();
        (await b).Dispose();
    }

    [Fact]
    public void Two_segments_of_one_venue_get_the_same_gate()
    {
        var gates = new VenueGates(new FakeTimeProvider(T0));

        // Both of these are segments of the venue 'kraken' — futures today, spot the day it is added.
        // They must contend for one budget, because they contend for one IP.
        var fromFutures = gates.For("kraken", 20, 8);
        var fromSpot = gates.For("kraken", 20, 8);
        Assert.Same(fromFutures, fromSpot);

        Assert.NotSame(fromFutures, gates.For("weex", 20, 8));
        Assert.Equal("weex", gates.For("weex", 20, 8).VenueCode);
    }

    [Fact]
    public void A_budget_edit_does_not_reshape_a_gate_that_already_exists()
    {
        var gates = new VenueGates(new FakeTimeProvider(T0));

        var built = gates.For("weex", 20, 8);
        var again = gates.For("weex", 99, 1);

        // Deliberate, and reported by the caller: a semaphore cannot be shrunk under leases already
        // granted, so the new numbers apply on restart. See VenueGates.
        Assert.Same(built, again);
        Assert.Equal(20, again.RequestsPerSecond);
        Assert.Equal(8, again.MaxConcurrentRequests);
        Assert.Same(built, gates.Existing("weex"));
        Assert.Null(gates.Existing("binance"));
    }

    [Fact]
    public async Task One_venue_being_held_back_does_not_slow_another()
    {
        var clock = new FakeTimeProvider(T0);
        var gates = new VenueGates(clock);
        var weex = gates.For("weex", 10, 4);
        var kraken = gates.For("kraken", 10, 4);

        weex.Penalize(TimeSpan.FromMinutes(5));

        var weexCaller = weex.AcquireAsync(CancellationToken.None).AsTask();
        var krakenCaller = kraken.AcquireAsync(CancellationToken.None).AsTask();

        Assert.True(await Granted(krakenCaller), "WEEX's 429 says nothing about Kraken's IP budget");
        Assert.True(await StillWaiting(weexCaller));

        (await krakenCaller).Dispose();
    }

    /// <summary>Completed within a generous real-time window — the gate's own waits are on the fake
    /// clock, so this only ever waits for a continuation to be scheduled.</summary>
    private static async Task<bool> Granted(Task<VenueLease> lease)
    {
        var finished = await Task.WhenAny(lease, Task.Delay(TimeSpan.FromSeconds(5)));
        return finished == (Task)lease;
    }

    /// <summary>Still not granted after a short real-time settle. Short because a false "waiting" is
    /// what a slow machine would produce, and every use of this asserts something the fake clock has
    /// already made true or not.</summary>
    private static async Task<bool> StillWaiting(Task<VenueLease> lease)
    {
        var finished = await Task.WhenAny(lease, Task.Delay(TimeSpan.FromMilliseconds(150)));
        return finished != (Task)lease;
    }
}

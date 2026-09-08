using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Hub.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The fail-fast contract every venue-gated sweep depends on: a pass where one item throws must
/// come back to the caller as a thrown exception, never as a smaller "successful" count. In
/// production that is <see cref="DepthCollector.RunAsync"/>, which needs Postgres and is proven on
/// the live stack instead (see <c>CollectFilterTests</c>'s doc comment on that split); the
/// concurrency-plus-fail-fast bookkeeping itself lives in the generic
/// <see cref="Sweep.RunAsync{T}"/> precisely so it has a database-free test.
///
/// This class exists because, before it, nothing guarded that bookkeeping: deleting the
/// <c>failure ??= ExceptionDispatchInfo.Capture(ex)</c> capture or the trailing
/// <c>failure?.Throw()</c> inside <c>Sweep.RunAsync</c> left every other test green while a sweep with a
/// hole in it silently reported a smaller written count as success — exactly the "ok=false became
/// ok" class of silent data loss this codebase forbids elsewhere. Verified by hand: with
/// <c>failure?.Throw();</c> temporarily deleted from <c>Sweep.RunAsync</c>,
/// <see cref="A_failing_item_still_fails_the_whole_sweep"/> fails (the awaited call returns 2
/// instead of throwing); restored byte-identical, it is green again.
/// </summary>
public sealed class SweepTests
{
    [Fact]
    public async Task A_failing_item_still_fails_the_whole_sweep()
    {
        var failed = new List<Exception>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sweep.RunAsync(
                items: new[] { 1, 2, 3, 4, 5 },
                maxConcurrent: 2,
                workAsync: async (item, ct) =>
                {
                    if (item == 3)
                    {
                        throw new InvalidOperationException("symbol 3 is broken");
                    }

                    await Task.Yield();
                    return 1;
                },
                onItemFailed: failed.Add,
                onFailure: SweepFailure.FailFast,
                ct: CancellationToken.None));

        // The real failure surfaces, not a cancellation from one of the siblings the failure
        // fast-cancelled — and it is thrown, not folded into a smaller "successful" count.
        Assert.Equal("symbol 3 is broken", thrown.Message);
        Assert.Single(failed);
    }

    [Fact]
    public async Task A_failing_sweep_makes_CollectorLoop_report_ok_false()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        var attempts = new List<CollectorAttempt>();

        // Stands in for DepthCollector.RunAsync's body: same Sweep.RunAsync, one item that always
        // fails. This is the chain the defect describes end to end, minus the Postgres-backed
        // ExchangeWorker.RecordGapAsync step, which — like the rest of that class — is proven on
        // the live stack rather than in this database-free test project.
        async Task<int> Body(CancellationToken ct) =>
            (await Sweep.RunAsync(
                items: new[] { "BTC", "broken-symbol", "ETH" },
                maxConcurrent: 2,
                workAsync: (symbol, _) => symbol == "broken-symbol"
                    ? throw new InvalidOperationException("venue returned 400 for broken-symbol")
                    : Task.FromResult(1),
                onItemFailed: _ => { },
                onFailure: SweepFailure.FailFast,
                ct: ct)).Written;

        using var cts = new CancellationTokenSource();
        var loop = new CollectorLoop(
            "weex-futures", "depth", () => TimeSpan.FromSeconds(60), Body,
            (a, _) =>
            {
                attempts.Add(a);
                cts.Cancel();
                return Task.CompletedTask;
            },
            NullLogger.Instance, clock);

        await loop.RunAsync(cts.Token);

        Assert.Single(attempts);
        Assert.False(attempts[0].Success);
        Assert.Equal(1, attempts[0].ConsecutiveFailures);
        Assert.Contains("venue returned 400 for broken-symbol", attempts[0].LastErrorOrEmpty(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the contract, and the reason the mode is a parameter: funding and candles
    /// have always let one broken symbol pass and kept walking (WEEX answers 400 for a live market's
    /// candles), and moving them off their own foreach onto this shared sweep must not quietly turn
    /// that into Depth's fail-fast. Everything after the failure still runs, and the caller gets the
    /// count, the tally and the last error to decide with.
    /// </summary>
    [Fact]
    public async Task Isolate_keeps_walking_past_a_failure_and_reports_it()
    {
        var result = await Sweep.RunAsync(
            items: new[] { 1, 2, 3, 4, 5 },
            maxConcurrent: 2,
            workAsync: async (item, ct) =>
            {
                await Task.Yield();
                return item == 3 ? throw new InvalidOperationException("symbol 3 is broken") : 1;
            },
            onItemFailed: _ => { },
            onFailure: SweepFailure.Isolate,
            ct: CancellationToken.None);

        Assert.Equal(4, result.Written);
        Assert.Equal(1, result.Failed);
        Assert.Equal("symbol 3 is broken", result.LastError?.Message);
    }

    /// <summary>
    /// The ceiling the venue gate is asked to enforce cannot be enforced by a walk that ignores it.
    /// Nothing here touches a venue — this pins the arithmetic: never more than maxConcurrent items
    /// in flight, and, when there is work for them, no fewer either. The second half matters as much
    /// as the first: a sweep that quietly ran one at a time would pass every other assertion in this
    /// file while leaving Kraken's funding pass at the 74 s it took before.
    /// </summary>
    [Fact]
    public async Task Never_more_than_the_ceiling_is_in_flight_and_the_ceiling_is_used()
    {
        var inFlight = 0;
        var peak = 0;
        var gate = new SemaphoreSlim(0);

        var result = await Sweep.RunAsync(
            items: Enumerable.Range(1, 40).ToArray(),
            maxConcurrent: 8,
            workAsync: async (_, ct) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peak, now);
                gate.Release();
                await Task.Delay(5, ct);
                Interlocked.Decrement(ref inFlight);
                return 1;
            },
            onItemFailed: _ => { },
            onFailure: SweepFailure.Isolate,
            ct: CancellationToken.None);

        Assert.Equal(40, result.Written);
        Assert.Equal(0, result.Failed);
        Assert.True(peak <= 8, $"{peak} items were in flight at once against a ceiling of 8");
        Assert.True(peak > 1, "the sweep never overlapped two calls; it walked serially");
    }

    /// <summary>Zero targets is not a failure and must not become one: a segment with nothing
    /// switched on writes ok=true and no gap, exactly as the per-symbol loops did with an empty
    /// foreach.</summary>
    [Fact]
    public async Task No_targets_is_an_empty_success()
    {
        var result = await Sweep.RunAsync(
            items: Array.Empty<string>(),
            maxConcurrent: 8,
            workAsync: (_, _) => throw new InvalidOperationException("must not be called"),
            onItemFailed: _ => { },
            onFailure: SweepFailure.FailFast,
            ct: CancellationToken.None);

        Assert.Equal(0, result.Written);
        Assert.Equal(0, result.Failed);
        Assert.Null(result.LastError);
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < candidate &&
               Interlocked.CompareExchange(ref target, candidate, seen) != seen)
        {
        }
    }
}

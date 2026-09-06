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
/// <see cref="DepthCollector.SweepAsync{T}"/> precisely so it has a database-free test.
///
/// This class exists because, before it, nothing guarded that bookkeeping: deleting the
/// <c>failure ??= ExceptionDispatchInfo.Capture(ex)</c> capture or the trailing
/// <c>failure?.Throw()</c> inside <c>SweepAsync</c> left every other test green while a sweep with a
/// hole in it silently reported a smaller written count as success — exactly the "ok=false became
/// ok" class of silent data loss this codebase forbids elsewhere. Verified by hand: with
/// <c>failure?.Throw();</c> temporarily deleted from <c>DepthCollector.SweepAsync</c>,
/// <see cref="A_failing_item_still_fails_the_whole_sweep"/> fails (the awaited call returns 2
/// instead of throwing); restored byte-identical, it is green again.
/// </summary>
public sealed class DepthCollectorSweepTests
{
    [Fact]
    public async Task A_failing_item_still_fails_the_whole_sweep()
    {
        var failed = new List<Exception>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DepthCollector.SweepAsync(
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

        // Stands in for DepthCollector.RunAsync's body: same SweepAsync, one item that always
        // fails. This is the chain the defect describes end to end, minus the Postgres-backed
        // ExchangeWorker.RecordGapAsync step, which — like the rest of that class — is proven on
        // the live stack rather than in this database-free test project.
        Task<int> Body(CancellationToken ct) =>
            DepthCollector.SweepAsync(
                items: new[] { "BTC", "broken-symbol", "ETH" },
                maxConcurrent: 2,
                workAsync: (symbol, _) => symbol == "broken-symbol"
                    ? throw new InvalidOperationException("venue returned 400 for broken-symbol")
                    : Task.FromResult(1),
                onItemFailed: _ => { },
                ct: ct);

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
}

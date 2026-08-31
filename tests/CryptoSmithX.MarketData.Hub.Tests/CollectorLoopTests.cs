using CryptoSmithX.MarketData.Hub.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Hub.Tests;

public sealed class CollectorLoopTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_healthy_collector_waits_exactly_one_interval()
    {
        Assert.Equal(Interval, CollectorLoop.DelayFor(Interval, 0));
    }

    [Fact]
    public void Backoff_doubles_per_consecutive_failure()
    {
        Assert.Equal(Interval * 2, CollectorLoop.DelayFor(Interval, 1));
        Assert.Equal(Interval * 4, CollectorLoop.DelayFor(Interval, 2));
    }

    [Fact]
    public void Backoff_stops_growing_at_five_intervals()
    {
        Assert.Equal(Interval * 5, CollectorLoop.DelayFor(Interval, 3));
        Assert.Equal(Interval * 5, CollectorLoop.DelayFor(Interval, 9));
        Assert.Equal(Interval * 5, CollectorLoop.DelayFor(Interval, 100));
        Assert.Equal(Interval * CollectorLoop.MaxBackoffFactor, CollectorLoop.DelayFor(Interval, 100));
    }

    [Fact]
    public async Task Failures_are_counted_and_a_success_resets_the_count()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        var attempts = new List<CollectorAttempt>();
        var calls = 0;

        // Fails three times, then succeeds, then is left alone.
        Task<int> Body(CancellationToken _)
        {
            calls++;
            return calls <= 3
                ? throw new InvalidOperationException($"venue said no ({calls})")
                : Task.FromResult(17);
        }

        using var cts = new CancellationTokenSource();
        var loop = new CollectorLoop(
            "fake", "snapshot", () => Interval, Body,
            (a, _) =>
            {
                attempts.Add(a);
                if (attempts.Count == 4)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            NullLogger.Instance, clock);

        var run = loop.RunAsync(cts.Token);
        for (var i = 0; i < 8 && !run.IsCompleted; i++)
        {
            clock.Advance(Interval * 5);
            await Task.Yield();
        }

        await run;

        Assert.Equal(4, attempts.Count);
        Assert.Equal([1, 2, 3, 0], attempts.Select(a => a.ConsecutiveFailures));
        Assert.Equal([false, false, false, true], attempts.Select(a => a.Success));
        Assert.All(attempts.Take(3), a => Assert.Contains("venue said no", a.LastErrorOrEmpty(), StringComparison.Ordinal));
        Assert.Null(attempts[3].Error);
        Assert.Equal(17, attempts[3].InstrumentsExpected);
        Assert.Equal(0, loop.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_failing_status_write_does_not_stop_the_loop()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        var bodyCalls = 0;

        using var cts = new CancellationTokenSource();
        var loop = new CollectorLoop(
            "fake", "candles", () => Interval,
            _ =>
            {
                bodyCalls++;
                if (bodyCalls == 3)
                {
                    cts.Cancel();
                }

                return Task.FromResult(1);
            },
            (_, _) => throw new InvalidOperationException("status table is on fire"),
            NullLogger.Instance, clock);

        var run = loop.RunAsync(cts.Token);
        for (var i = 0; i < 8 && !run.IsCompleted; i++)
        {
            clock.Advance(Interval);
            await Task.Yield();
        }

        await run;
        Assert.True(bodyCalls >= 3, $"the loop stopped after {bodyCalls} iterations");
    }
}

internal static class AttemptExtensions
{
    public static string LastErrorOrEmpty(this CollectorAttempt a) => a.Error ?? "";
}

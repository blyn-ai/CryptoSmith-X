using CryptoSmithX.MarketData.Connectors.Pacing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Where a background feed gets its work from, which until now was the venue's own listing: all 566
/// Binance perpetuals while 44 are collected, all ~990 WEEX contracts while 25 are, all 178
/// Hyperliquid coins while 25 are. Most of every pass was spent sampling instruments nothing stores,
/// paid for out of the same request budget the collector loops queue behind.
///
/// The three feeds are now thin wrappers over <see cref="SymbolCycle"/> — a delegate in, a sample
/// lambda out — so the thing worth pinning is this class: it samples exactly what the delegate
/// returns, and it has no way to ask a venue what exists, because it is never given a client.
/// </summary>
public sealed class SymbolCycleTests
{
    private static VenueGate Gate(int concurrency = 4) =>
        new("test", requestsPerSecond: 1000, maxConcurrentRequests: concurrency, TimeProvider.System);

    [Fact]
    public async Task It_samples_exactly_the_symbols_the_delegate_returns()
    {
        var sampled = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var cts = new CancellationTokenSource();

        await SymbolCycle.RunAsync(
            "test", _ => Task.FromResult(new[] { "BTC", "ETH", "SOL" }),
            TimeSpan.FromMinutes(10), Gate(),
            (symbol, _) =>
            {
                sampled.Add(symbol);
                // One full pass is all this asserts; the cycle itself is infinite by design.
                if (sampled.Count >= 3)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            NullLogger.Instance, TimeProvider.System, cts.Token);

        Assert.Equal(["BTC", "ETH", "SOL"], sampled.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The list comes from a database on the other side of a socket, and a blip there must not blank
    /// a feed the snapshot depends on: the previous set keeps being sampled. Before the delegate, the
    /// same rule protected the venue listing — it is the reason this is a warning and not a throw.
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_keeps_the_previous_set()
    {
        var calls = 0;
        var sampled = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var cts = new CancellationTokenSource();

        await SymbolCycle.RunAsync(
            "test",
            _ => ++calls == 1
                ? Task.FromResult(new[] { "BTC" })
                : Task.FromException<string[]>(new InvalidOperationException("database is away")),
            // Zero, so every iteration re-reads and the second read is the failing one.
            TimeSpan.Zero, Gate(),
            (symbol, _) =>
            {
                sampled.Add(symbol);
                if (sampled.Count >= 2)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            NullLogger.Instance, TimeProvider.System, cts.Token);

        Assert.True(calls >= 2, "the cycle never attempted a second refresh");
        Assert.Equal(2, sampled.Count);
        Assert.All(sampled, s => Assert.Equal("BTC", s));
    }

    /// <summary>A symbol that throws leaves the rest of the pass alone — the isolation the feeds had
    /// in their own loops, kept when they moved onto the shared sweep.</summary>
    [Fact]
    public async Task One_failing_symbol_does_not_stop_the_pass()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var cts = new CancellationTokenSource();

        await SymbolCycle.RunAsync(
            "test", _ => Task.FromResult(new[] { "BTC", "BROKEN", "ETH" }),
            TimeSpan.FromMinutes(10), Gate(concurrency: 1),
            (symbol, _) =>
            {
                seen.Add(symbol);
                if (seen.Count >= 3)
                {
                    cts.Cancel();
                }

                return symbol == "BROKEN"
                    ? Task.FromException(new HttpRequestException("venue said 400"))
                    : Task.CompletedTask;
            },
            NullLogger.Instance, TimeProvider.System, cts.Token);

        Assert.Equal(["BROKEN", "BTC", "ETH"], seen.Order(StringComparer.Ordinal));
    }
}

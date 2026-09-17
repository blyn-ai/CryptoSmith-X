using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Hub.Ingestion;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The depth pass keeps what it fetched. Before 2026-09-17 one refused symbol out of 35 cancelled the
/// pass and discarded the 34 books already in hand; on production OKX that was about one pass in five.
/// <see cref="DepthCollector.RunAsync"/> needs Postgres, so the fetch contract lives in
/// <see cref="DepthCollector.FetchAsync{T, TBook}"/> and is pinned here.
/// </summary>
public sealed class DepthFetchTests
{
    private static VenueGate Gate() => new("test.example", 1000, 4, TimeProvider.System);

    private static VenueRateLimitedException Refusal() =>
        new("too fast", TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task A_refused_symbol_is_retried_and_nothing_is_lost()
    {
        var calls = new Dictionary<string, int>();
        var outcome = await DepthCollector.FetchAsync(
            new[] { "A", "B", "C", "D", "E" },
            Gate(),
            (symbol, _) =>
            {
                lock (calls)
                {
                    calls[symbol] = calls.GetValueOrDefault(symbol) + 1;
                    if (symbol == "C" && calls[symbol] == 1)
                    {
                        throw Refusal();
                    }
                }

                return Task.FromResult<string?>("book " + symbol);
            },
            CancellationToken.None);

        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, outcome.Fetched.Select(f => f.Item));
        Assert.Empty(outcome.Missing);
        Assert.Equal(2, calls["C"]);
        Assert.Equal(1, calls["A"]);
    }

    [Fact]
    public async Task A_symbol_that_keeps_failing_is_reported_and_the_rest_are_kept()
    {
        var outcome = await DepthCollector.FetchAsync(
            new[] { "A", "B", "C", "D" },
            Gate(),
            (symbol, _) => symbol switch
            {
                "B" => throw Refusal(),
                "D" => throw new InvalidOperationException("broken symbol"),
                _ => Task.FromResult<string?>("book " + symbol)
            },
            CancellationToken.None);

        Assert.Equal(new[] { "A", "C" }, outcome.Fetched.Select(f => f.Item));
        Assert.Equal(new[] { "B", "D" }, outcome.Missing.Select(m => m.Item));
        Assert.IsType<VenueRateLimitedException>(outcome.Missing[0].Error);
        Assert.IsType<InvalidOperationException>(outcome.Missing[1].Error);
    }

    [Fact]
    public async Task Only_refusals_are_retried()
    {
        var calls = 0;
        var outcome = await DepthCollector.FetchAsync<string, string>(
            new[] { "A" },
            Gate(),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("broken symbol");
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Single(outcome.Missing);
    }

    [Fact]
    public async Task A_null_book_is_neither_fetched_nor_missing()
    {
        var outcome = await DepthCollector.FetchAsync(
            new[] { "A", "B" },
            Gate(),
            (symbol, _) => Task.FromResult(symbol == "A" ? null : "book"),
            CancellationToken.None);

        Assert.Equal(new[] { "B" }, outcome.Fetched.Select(f => f.Item));
        Assert.Empty(outcome.Missing);
    }
}

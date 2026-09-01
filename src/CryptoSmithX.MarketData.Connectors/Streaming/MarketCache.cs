using System.Collections.Concurrent;

namespace CryptoSmithX.MarketData.Connectors.Streaming;

/// <summary>
/// A thread-safe "symbol → (value, when it arrived)" cache for a streaming feed, venue-agnostic.
/// The central honesty rule of the WS path lives here: a reader asks only for entries younger than a
/// threshold, so a frozen socket surfaces as staleness instead of looking fresh. Timestamps come from
/// an injected <see cref="TimeProvider"/> so freshness is testable without a clock.
/// </summary>
public sealed class MarketCache<T>
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public MarketCache(TimeProvider clock) => _clock = clock;

    public void Set(string symbol, T value) => _entries[symbol] = new Entry(value, _clock.GetUtcNow());

    public void Remove(string symbol) => _entries.TryRemove(symbol, out _);

    public int Count => _entries.Count;

    /// <summary>The value for a symbol, only if it is younger than <paramref name="maxAge"/>.</summary>
    public bool TryGet(string symbol, TimeSpan maxAge, out T value)
    {
        if (_entries.TryGetValue(symbol, out var entry) && _clock.GetUtcNow() - entry.At <= maxAge)
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Every value younger than <paramref name="maxAge"/>, in no particular order.</summary>
    public IReadOnlyList<T> FresherThan(TimeSpan maxAge)
    {
        var cutoff = _clock.GetUtcNow() - maxAge;
        var list = new List<T>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            if (entry.At >= cutoff)
            {
                list.Add(entry.Value);
            }
        }

        return list;
    }

    /// <summary>How many entries are younger than <paramref name="maxAge"/> — the health signal.</summary>
    public int FreshCount(TimeSpan maxAge)
    {
        var cutoff = _clock.GetUtcNow() - maxAge;
        var n = 0;
        foreach (var entry in _entries.Values)
        {
            if (entry.At >= cutoff)
            {
                n++;
            }
        }

        return n;
    }

    private readonly record struct Entry(T Value, DateTimeOffset At);
}

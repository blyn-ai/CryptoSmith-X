using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Hub.Live;

/// <summary>
/// One subscriber's "what has actually changed since I last told them" — last-write-wins per
/// instrument, and the reason a slow reader costs a skipped frame rather than a growing queue.
///
/// <b>The conflation itself is free and happens upstream.</b> The feeds write into
/// <see cref="Connectors.Streaming.MarketCache{T}"/>, which keeps one entry per symbol; three
/// venue messages between two ticks leave one value behind, not three. Polling that cache on a
/// fixed tick IS the conflation — there is no queue to drain and nothing to drop. What is left for
/// this class is the second half: not sending a value the subscriber already has.
///
/// <b>Why identity includes the timestamp.</b> A venue re-sending the same price at a new instant
/// is news, because the page shows how old the figure is: suppressing that frame would leave the
/// live age climbing under a socket that is perfectly healthy, which reads as staleness that is not
/// there. So a <see cref="LiveQuote"/> is compared whole, and "unchanged" means the feed's cache
/// entry has not been written at all since the last tick — exactly the case worth staying quiet
/// about.
/// </summary>
public sealed class Conflator
{
    private readonly Dictionary<int, LiveQuote> _lastSent = [];

    /// <summary>How many instruments this subscriber has been told about at least once.</summary>
    public int Known => _lastSent.Count;

    /// <summary>The subset of <paramref name="polled"/> worth putting on the wire, remembering it as
    /// sent. Called once per tick per connection; an empty result means no frame at all.</summary>
    public IReadOnlyList<(int InstrumentId, LiveQuote Quote)> Changed(
        IEnumerable<(int InstrumentId, LiveQuote Quote)> polled)
    {
        List<(int, LiveQuote)>? changed = null;
        foreach (var (id, quote) in polled)
        {
            if (_lastSent.TryGetValue(id, out var previous) && previous.Equals(quote))
            {
                continue;
            }

            _lastSent[id] = quote;
            (changed ??= []).Add((id, quote));
        }

        return (IReadOnlyList<(int, LiveQuote)>?)changed ?? [];
    }

    /// <summary>Forget everything sent, so the next tick is a whole picture again — what a
    /// reconnected subscriber needs, since it cannot be assumed to still hold what it was told
    /// before the gap.</summary>
    public void Reset() => _lastSent.Clear();
}

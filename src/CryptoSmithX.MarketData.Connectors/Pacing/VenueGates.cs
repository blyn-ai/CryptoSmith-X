using System.Collections.Concurrent;

namespace CryptoSmithX.MarketData.Connectors.Pacing;

/// <summary>
/// One <see cref="VenueGate"/> per venue, for the lifetime of the process. The registry exists so the
/// Hub's collectors and the connectors' own background feeds end up holding the SAME gate for one
/// venue — a per-caller gate would be four ceilings pretending to be one, which is precisely the
/// arrangement 0019 was written to end.
///
/// A gate is created once, from the budget in force at that moment, and then kept: an exchange
/// disabled and re-enabled in the console keeps its schedule, because the venue's IP budget does not
/// reset when we stop looking. Editing the budget in the database therefore takes effect on restart,
/// not live — the concurrency ceiling is a semaphore that cannot be shrunk under leases already
/// granted, and a gate that honoured half a change would be worse than one that says so out loud.
/// <see cref="Existing"/> lets a caller notice the difference and log it.
/// </summary>
public sealed class VenueGates
{
    private readonly ConcurrentDictionary<string, VenueGate> _byVenue = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public VenueGates(TimeProvider clock) => _clock = clock;

    /// <summary>The gate for this venue, created from these numbers only if it does not exist yet.</summary>
    public VenueGate For(string venueCode, int requestsPerSecond, int maxConcurrentRequests) =>
        _byVenue.GetOrAdd(
            venueCode,
            static (code, args) => new VenueGate(code, args.Rps, args.Concurrency, args.Clock),
            (Rps: requestsPerSecond, Concurrency: maxConcurrentRequests, Clock: _clock));

    /// <summary>The gate already built for this venue, or null. For reporting a budget edit that is
    /// waiting on a restart — never for deciding whether to create one.</summary>
    public VenueGate? Existing(string venueCode) => _byVenue.GetValueOrDefault(venueCode);
}

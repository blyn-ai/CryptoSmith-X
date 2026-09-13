using System.Collections.Concurrent;

namespace CryptoSmithX.MarketData.Connectors.Pacing;

/// <summary>
/// One <see cref="VenueGate"/> per HOST, for the lifetime of the process. The registry exists so the
/// Hub's collectors and the connectors' own background feeds end up holding the SAME gate for one
/// host — a per-caller gate would be four ceilings pretending to be one, which is precisely the
/// arrangement 0019 was written to end.
///
/// <b>Per host, not per venue, and that is a correction.</b> 0019 put the budget on the venue
/// because two segments of one exchange share one IP budget, and for perpetuals they do. Spot broke
/// it in both directions at once, measured:
///
///   Binance   fapi.binance.com and api.binance.com carry SEPARATE ceilings — 2 400 and 6 000
///             weight a minute. Keyed on the venue, two independent budgets become one and both
///             surfaces are throttled to a ceiling neither of them has.
///   OKX       www.okx.com serves SWAP and SPOT alike. Twenty concurrent `tickers` split across
///             the two surfaces returned ten successes each — one ceiling, shared. Keyed on the
///             segment, we would hand out two gates where the venue counts one.
///   Bybit     api.bybit.com for both surfaces, like OKX.
///   Kraken    futures.kraken.com and api.kraken.com, like Binance.
///
/// The venue key is wrong for two of those and the segment key is wrong for the other two. The host
/// is right for all four. 0053 reached the same conclusion from the other end — it gave Coinbase
/// International its own <c>exchange</c> row precisely because it has its own host and its own
/// ceiling — and that split stops being necessary once the key is the thing the ceiling belongs to.
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
    private readonly ConcurrentDictionary<string, VenueGate> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public VenueGates(TimeProvider clock) => _clock = clock;

    /// <summary>The gate for this host, created from these numbers only if it does not exist yet.
    /// Case-insensitive, because a hostname is.</summary>
    public VenueGate For(string host, int requestsPerSecond, int maxConcurrentRequests) =>
        _byHost.GetOrAdd(
            host,
            static (key, args) => new VenueGate(key, args.Rps, args.Concurrency, args.Clock),
            (Rps: requestsPerSecond, Concurrency: maxConcurrentRequests, Clock: _clock));

    /// <summary>The gate already built for this host, or null. For reporting a budget edit that is
    /// waiting on a restart — never for deciding whether to create one.</summary>
    public VenueGate? Existing(string host) => _byHost.GetValueOrDefault(host);
}

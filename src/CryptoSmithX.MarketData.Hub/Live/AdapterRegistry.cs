using System.Collections.Concurrent;
using CryptoSmithX.MarketData.Connectors;

namespace CryptoSmithX.MarketData.Hub.Live;

/// <summary>
/// Which adapters are running right now, by segment code — the one seam between the collection
/// supervisor and the live egress.
///
/// <b>Why a registry and not an injected list.</b> Adapters are not configuration: <c>ExchangeWorker</c>
/// builds one when an exchange is switched on and drops it when it is switched off, sockets and all,
/// without a restart. A list resolved once at startup would hand the egress an adapter whose token
/// has been cancelled — a feed that answers nothing forever while the console shows the exchange
/// running. So the worker publishes into this as it starts a segment and withdraws on the way out,
/// and the egress reads nothing else.
///
/// It carries no <c>VenueGate</c> on purpose. A gate is a REST request ceiling, and the live path
/// makes no requests — see <see cref="IExchangeMarketData.LiveQuotes"/>. Handing one over here would
/// suggest there is something to pace.
/// </summary>
public interface IAdapterRegistry
{
    /// <summary>The adapter serving this segment, or false when the exchange is not running.</summary>
    bool TryGet(string segmentCode, out IExchangeMarketData adapter);

    /// <summary>Every segment with a running adapter, in no particular order.</summary>
    IReadOnlyCollection<string> Segments { get; }
}

/// <summary>The registry as the worker fills it. A plain concurrent dictionary: the writer is the
/// reconcile pass (one thread, every 30 s) and the readers are egress connections.</summary>
public sealed class AdapterRegistry : IAdapterRegistry
{
    private readonly ConcurrentDictionary<string, IExchangeMarketData> _adapters = new(StringComparer.Ordinal);

    public bool TryGet(string segmentCode, out IExchangeMarketData adapter) =>
        _adapters.TryGetValue(segmentCode, out adapter!);

    public IReadOnlyCollection<string> Segments => (IReadOnlyCollection<string>)_adapters.Keys;

    public void Publish(string segmentCode, IExchangeMarketData adapter) => _adapters[segmentCode] = adapter;

    public void Withdraw(string segmentCode) => _adapters.TryRemove(segmentCode, out _);
}

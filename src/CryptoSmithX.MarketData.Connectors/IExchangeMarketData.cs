using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors;

/// <summary>
/// Everything the hub needs from a venue. Public endpoints only — this service holds no keys and
/// places no orders. Implementations translate the wire format into the canonical records and
/// normalise units to what the DDL documents.
/// </summary>
public interface IExchangeMarketData
{
    /// <summary>Matches <c>exchange.code</c>.</summary>
    string ExchangeCode { get; }

    /// <summary>
    /// Which <c>collection.code</c>s this adapter honestly implements, and over which transport(s) —
    /// a fixed fact about the adapter instance, not something that changes at runtime. A collection
    /// absent from this list gets <c>we_implement=false</c> when <c>ExchangeWorker</c> declares
    /// capability into <c>exchange_collection_capability</c> (0014); the fake's depth is the standing
    /// example: <see cref="GetOrderBookAsync"/> always returns null, so it declares no depth entry
    /// here even though a <c>DepthCollector</c> could technically be pointed at it.
    /// </summary>
    IReadOnlyList<CollectionCapability> Capabilities { get; }

    /// <summary>Linear perpetuals with a USD-family quote. Dated and inverse contracts are skipped.</summary>
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct);

    /// <summary>Every instrument in one call where the venue allows it.</summary>
    Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct);

    /// <summary>Closed 1-minute bars only; a bar still forming is never returned.</summary>
    Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    /// <summary>
    /// Historical funding payments in [from, to], oldest first. Venues serve these back in time,
    /// so the Hub can back-fill the series rather than only recording the live rate.
    /// </summary>
    Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    /// <summary>
    /// The order book for one instrument, reduced to the cumulative-notional bands the snapshot
    /// stores, or null when the venue carries the book inline in its ticker and has no separate call
    /// (the fake). A per-symbol call: the depth collector paces it and asks only for trading
    /// instruments, so it lives apart from <see cref="GetTickersAsync"/>.
    /// </summary>
    Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct);
}

/// <summary>One declared capability: this adapter implements <paramref name="CollectionCode"/>, using
/// <paramref name="TransportsUs"/> (comma-joined, e.g. "rest" or "rest,ws" — matches the <c>list</c>
/// kind of <c>capability_key</c> in 0014).</summary>
public sealed record CollectionCapability(string CollectionCode, string TransportsUs);

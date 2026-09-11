namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// What the adapter needs from the venue's live catalogue feed: the newest whole catalogue, and the
/// Pyth symbol behind a pair. False means "no fresh copy", and the adapter falls back to the one
/// REST call — the same WS-first / REST-fallback seam Kraken, WEEX, Hyperliquid and Binance already
/// provide, so the adapter's branch is testable without a socket.
///
/// <b>It carries no price, and that is a fact about the venue.</b> Connecting to it on 2026-09-11
/// returned 86 KB of <c>RES:DATA</c> holding <c>pairInfos</c> with no occurrence of price, index,
/// mid or last anywhere in the payload. This feed is open interest, funding, spreads and trading
/// hours — everything the catalogue has, which is everything the venue publishes about its markets
/// except what they cost.
/// </summary>
public interface IAvantisCatalogFeed
{
    /// <summary>The freshest catalogue the socket has assembled, or false when it has none — a
    /// connection still opening, a feed that dropped, or a process that has not received its first
    /// full snapshot yet.</summary>
    bool TryGetCatalog(out AvantisCatalog? catalog);

    /// <summary>The Pyth symbol (<c>Crypto.BTC/USD</c>) behind a venue pair, which is how the
    /// candle shim is addressed. Kept here because the catalogue is the only thing that knows it.</summary>
    bool TryGetPythSymbol(string exchangeSymbol, out string? pythSymbol);
}

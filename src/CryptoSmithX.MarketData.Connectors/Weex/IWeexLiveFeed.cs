using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// What the adapter needs from the live WEEX WebSocket feed: depth for one symbol, returning false
/// whenever the feed cannot honestly serve it so the adapter falls back to the REST book. An
/// interface, not the concrete feed, so the adapter's WS-first / REST-fallback branch is testable
/// without a socket — the same seam <see cref="IWeexOpenInterestFeed"/> and
/// <see cref="Kraken.IKrakenLiveFeed"/> already provide.
///
/// Depth only, and that is the whole surface on purpose. WEEX's V3 socket has no top-of-book
/// channel at all (@bookTicker, @markPrice and @miniTicker are all rejected — see the captured
/// Fixtures/weex-ws/README.md), and it carries neither funding rate nor open interest, so a WS-fed
/// snapshot would still be the same three REST calls plus a fourth clock to reconcile. The
/// snapshot's REST path is one batched call per venue and was never the expensive one; the order
/// book was — one call per symbol, 361 s per sweep on production.
/// </summary>
public interface IWeexLiveFeed
{
    bool TryGetDepth(string symbol, out Depth depth);
}

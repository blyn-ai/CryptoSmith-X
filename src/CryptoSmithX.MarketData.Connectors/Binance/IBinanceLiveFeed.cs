using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// What the adapter needs from the live Binance WebSocket feed: depth for one symbol, returning false
/// whenever the feed cannot honestly serve it so the adapter falls back to the REST book. An
/// interface, not the concrete feed, so the adapter's WS-first / REST-fallback branch is testable
/// without a socket — the same seam <see cref="Kraken.IKrakenLiveFeed"/>,
/// <see cref="Hyperliquid.IHyperliquidLiveFeed"/> and <see cref="Weex.IWeexLiveFeed"/> provide.
///
/// Depth only, and the narrowness is a decision rather than an unfinished edge. Binance's socket
/// certainly could serve the snapshot: <c>!bookTicker</c> carries top of book, <c>!markPrice@arr</c>
/// carries mark, index and funding, <c>!ticker@arr</c> carries turnover. But those three streams live
/// on two differently ROUTED endpoints and tick on three different clocks, so a WS-fed snapshot means
/// merging three caches and choosing one <c>received_at</c> for the result — the exact shape of
/// reasoning that produced the reviewed defect "ReceivedAt = min of the batched clocks silently loses
/// observations". Against that, the REST snapshot is three batched calls costing 55 weight of a
/// 2400/minute budget: affordable several times a minute and never the expensive part. The order book
/// is the expensive part, it is the one dataset that cannot be re-fetched at any price, and on this
/// venue it is also the one the REST path answers WORST — 100 levels of BTCUSDT span 1.4 bps, so
/// every band comes back null. That is what the socket is for.
/// </summary>
public interface IBinanceLiveFeed
{
    bool TryGetDepth(string symbol, out Depth depth);
}

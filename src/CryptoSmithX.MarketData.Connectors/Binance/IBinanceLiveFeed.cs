using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// What the adapter needs from the live Binance WebSocket feed: depth for one symbol, returning false
/// whenever the feed cannot honestly serve it so the adapter falls back to the REST book. An
/// interface, not the concrete feed, so the adapter's WS-first / REST-fallback branch is testable
/// without a socket — the same seam <see cref="Kraken.IKrakenLiveFeed"/>,
/// <see cref="Hyperliquid.IHyperliquidLiveFeed"/> and <see cref="Weex.IWeexLiveFeed"/> provide.
///
/// Depth only. Not narrowness by decision any more — the ticker context and candles this doc comment
/// used to argue should stay on REST moved to WS after all, but on a SEPARATE interface and a
/// SEPARATE connection: see <see cref="IBinanceMarketFeed"/>, implemented by
/// <see cref="BinanceMarketWsFeed"/> against <c>/market/stream</c>. The two-different-clocks concern
/// this comment used to raise did not go away; it is resolved the same way
/// <c>BinanceUsdmMarketData.GetTickersAsync</c>'s REST path already resolved it for its own
/// three-source merge — the composed row is stamped with OUR receive time, never a venue clock, so
/// there is nothing to choose between. <c>!bookTicker</c> (bid/ask) stays on REST regardless: neither
/// socket carries a book on the second connection, so bid/ask has no WS source to move to.
///
/// This interface stays depth-only because depth is where the two sockets genuinely cannot be one
/// class: seeding a book from REST and validating the first frame against it (see the class remarks
/// below) is machinery <c>/market/stream</c>'s pure streams have no version of at all.
/// </summary>
public interface IBinanceLiveFeed
{
    bool TryGetDepth(string symbol, out Depth depth);
}

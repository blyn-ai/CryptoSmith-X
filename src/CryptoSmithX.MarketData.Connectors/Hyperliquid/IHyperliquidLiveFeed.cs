using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>Top of book for one symbol, at the moment it was sampled.</summary>
public sealed record BookTop(double BidPrice, double BidSize, double AskPrice, double AskSize);

/// <summary>
/// What the adapter needs for bid/ask/size and depth: <c>metaAndAssetCtxs</c> batches mark, oracle,
/// funding and open interest, but carries no book at all, so both the top of book (for the ticker) and
/// the cumulative-notional depth come from this feed instead — one <c>l2Book</c> response serves both,
/// computed once per fetch. Two implementations share this contract: <see cref="HyperliquidBookFeed"/>
/// (REST polling — the Phase 1 baseline, no WS required) and <see cref="HyperliquidWsFeed"/> (Phase 2 —
/// the live socket, preferred when healthy). False means "no fresh sample" either way, so the adapter's
/// merge-and-skip logic does not care which implementation it holds.
/// </summary>
public interface IHyperliquidLiveFeed
{
    bool TryGetTop(string symbol, out BookTop top);

    bool TryGetDepth(string symbol, out Depth depth);
}

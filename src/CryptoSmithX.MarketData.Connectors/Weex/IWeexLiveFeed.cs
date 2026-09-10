using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// What the adapter needs from the live WEEX WebSocket feed: depth for one symbol, returning false
/// whenever the feed cannot honestly serve it so the adapter falls back to the REST book. An
/// interface, not the concrete feed, so the adapter's WS-first / REST-fallback branch is testable
/// without a socket — the same seam <see cref="IWeexOpenInterestFeed"/> and
/// <see cref="Kraken.IKrakenLiveFeed"/> already provide.
///
/// Depth AND 1-minute candles — not snapshot. WEEX's V3 socket has no top-of-book channel at all
/// (@bookTicker, @markPrice and @miniTicker are all rejected — see the captured
/// Fixtures/weex-ws/README.md), and it carries neither funding rate nor open interest, so a WS-fed
/// snapshot would still be the same three REST calls plus a fourth clock to reconcile. The
/// snapshot's REST path is one batched call per venue and was never the expensive one. Depth and
/// candles were: one call per symbol each, 361 s per sweep for depth and 990 requests a minute for
/// candles at REST's own pace.
/// </summary>
public interface IWeexLiveFeed
{
    bool TryGetDepth(string symbol, out Depth depth);

    /// <summary>Every 1-minute bar in <c>[from, to)</c> from the live <c>@kline_1m</c> stream, or
    /// false when the feed cannot honestly serve the WHOLE range — see
    /// <see cref="Market.CandleCache.TryGetRange"/> for why a partial answer is refused rather than
    /// thinned. The caller falls back to REST for the entire call.</summary>
    bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles);

    /// <summary>Trades buffered since the last call — see <see cref="IExchangeMarketData.DrainTrades"/>.
    /// Defaulted so a test double that only cares about depth stays small.</summary>
    IReadOnlyList<TradeEvent> DrainTrades() => [];

    /// <summary>A lossy second reader of the same tape, for the live path — see
    /// <see cref="IExchangeMarketData.ObserveTrades"/>. Null on a feed with no socket behind it.</summary>
    Streaming.EventTap<TradeEvent>? ObserveTrades(int capacity) => null;

    bool TryGetBookFrame(string symbol, int levels, out BookFrame frame)
    {
        frame = null!;
        return false;
    }
}

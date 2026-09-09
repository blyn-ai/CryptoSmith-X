using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>Top of book for one symbol, at the moment it was sampled.</summary>
public sealed record BookTop(double BidPrice, double BidSize, double AskPrice, double AskSize);

/// <summary>
/// Mark/oracle/funding/open-interest for one coin, plus the book-mid proxy this venue uses in place
/// of a last-trade price (see <see cref="HyperliquidMarketData"/>'s class remarks). Carries its own
/// <see cref="Symbol"/> and <see cref="At"/> so a whole batch of these can be handed back as one
/// list — the same shape <c>KrakenWsFeed.TryGetFreshTickers</c> already uses for its ticker cache.
/// </summary>
public sealed record AssetContext(
    string Symbol,
    double LastPrice,
    double MarkPrice,
    double IndexPrice,
    double FundingRate,
    double Turnover24h,
    double OpenInterest,
    DateTimeOffset At);

/// <summary>
/// What the adapter needs from a live Hyperliquid feed: book (top and depth — <c>metaAndAssetCtxs</c>
/// batches mark, oracle, funding and open interest but carries no book at all, so both the ticker's
/// bid/ask/size and the cumulative-notional depth come from here), ticker context batched across the
/// whole venue, and 1-minute candles. Three implementations share this contract:
/// <see cref="HyperliquidBookFeed"/> (REST polling — the always-available baseline, book only) and
/// <see cref="HyperliquidWsFeed"/> (the live socket, preferred when healthy, all four). False means
/// "no fresh sample" across every method, so the adapter's merge-and-skip logic does not care which
/// implementation it holds.
/// </summary>
public interface IHyperliquidLiveFeed
{
    bool TryGetTop(string symbol, out BookTop top);

    bool TryGetDepth(string symbol, out Depth depth);

    /// <summary>Every coin this feed currently has a healthy, whole-venue answer for, or false
    /// meaning "ask REST for the whole ticker instead" — the same ternary
    /// <c>KrakenWsFeed.TryGetFreshTickers</c> uses. <see cref="HyperliquidBookFeed"/>, the REST
    /// baseline, always returns false: it polls the book only, and <c>GetTickersAsync</c>'s own
    /// <c>metaAndAssetCtxs</c> call already IS the context baseline, so a second one here would just
    /// be that same REST call wearing an interface.</summary>
    bool TryGetFreshContexts(out IReadOnlyList<AssetContext> contexts);

    /// <summary>Every 1-minute bar in <c>[from, to)</c> from the live <c>candle</c> stream, or false
    /// when the feed cannot honestly serve the WHOLE range — see
    /// <see cref="Market.CandleCache.TryGetRange"/> for why a partial answer is refused rather than
    /// thinned. The caller falls back to REST for the entire call.</summary>
    bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles);
}

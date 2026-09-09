using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>Last price, mark, index, funding and turnover for one symbol, batched across the whole
/// venue — the two array streams (<c>!ticker@arr</c>, <c>!markPrice@arr@1s</c>) that fill this record
/// tick on different clocks, so <see cref="At"/> is OUR receive time at the moment both were merged,
/// not either venue clock — the same reasoning <c>BinanceUsdmMarketData.GetTickersAsync</c>'s REST
/// path already documents for its own three-source merge.</summary>
/// <param name="VenueTs">The <c>!markPrice@arr@1s</c> entry's own event time ("E") — unlike
/// <see cref="At"/> (our merge time), this is the venue's clock, and it is the mark/index/funding
/// entry's time specifically since that stream is the source for those three fields.</param>
/// <param name="NextFundingAt">The <c>!markPrice@arr@1s</c> entry's "T" — when the next funding
/// payment settles.</param>
/// <param name="Volume24hBase">The <c>!ticker@arr</c> entry's "v" — 24h turnover in the base asset,
/// independent of <see cref="Turnover24h"/> (quote asset, "q").</param>
public sealed record BinanceContext(
    string Symbol,
    double LastPrice,
    double MarkPrice,
    double IndexPrice,
    double FundingRate,
    double Turnover24h,
    DateTimeOffset At,
    DateTimeOffset? VenueTs = null,
    DateTimeOffset? NextFundingAt = null,
    double? Volume24hBase = null);

/// <summary>
/// What the adapter needs from Binance's SECOND socket (<c>/market/stream</c>) — ticker context and
/// candles. A separate interface from <see cref="IBinanceLiveFeed"/> rather than an extension of it,
/// because these are genuinely a separate connection with a separate lifecycle: <c>/public/stream</c>
/// (depth, <see cref="BinanceWsFeed"/>) and <c>/market/stream</c> (this one) are two sockets Binance
/// itself keeps apart by routing, and one going down says nothing about the other. Only one
/// implementation, <see cref="BinanceMarketWsFeed"/> — this venue's REST fallback for ticker/candles
/// is already the adapter's own direct calls, so there is no REST-baseline class to share the
/// contract with, unlike Hyperliquid's <c>HyperliquidBookFeed</c>.
/// </summary>
public interface IBinanceMarketFeed
{
    /// <summary>Every symbol this feed currently has a healthy, whole-venue answer for, or false
    /// meaning "ask REST instead" — the same ternary Kraken's ticker cache uses.</summary>
    bool TryGetFreshContexts(out IReadOnlyList<BinanceContext> contexts);

    /// <summary>Every 1-minute bar in <c>[from, to)</c> from the live <c>kline_1m</c> stream, or
    /// false when the feed cannot honestly serve the WHOLE range — see
    /// <see cref="Market.CandleCache.TryGetRange"/> for why a partial answer is refused rather than
    /// thinned. The caller falls back to REST for the entire call.</summary>
    bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles);
}

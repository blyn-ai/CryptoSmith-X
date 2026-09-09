namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// A closed bar. Mirrors <c>market_candle</c>; adapters only ever produce 1-minute bars, so
/// timeframe and bar_count are the store's concern.
/// </summary>
/// <param name="TradeCount">Null where the venue does not report it (Kraken Futures, WEEX).</param>
/// <param name="VolumeQuote">Bar turnover in the quote asset, independent of <paramref name="Volume"/>
/// (the base-asset/quote-asset price is not constant across the minute, only at a ticker's instant).
/// Null where the venue does not report it at this scale — Kraken and Hyperliquid, on both REST and
/// WS, at every timeframe.</param>
public sealed record Candle(
    string ExchangeSymbol,
    DateTimeOffset OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    int? TradeCount,
    double? VolumeQuote = null);

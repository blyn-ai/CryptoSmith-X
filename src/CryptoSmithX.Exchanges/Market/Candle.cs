namespace CryptoSmithX.Exchanges.Market;

/// <summary>
/// A closed bar. Mirrors <c>market_candle</c>; adapters only ever produce 1-minute bars, so
/// timeframe and bar_count are the store's concern.
/// </summary>
/// <param name="TradeCount">Null where the venue does not report it (Kraken Futures, WEEX).</param>
public sealed record Candle(
    string ExchangeSymbol,
    DateTimeOffset OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    int? TradeCount);

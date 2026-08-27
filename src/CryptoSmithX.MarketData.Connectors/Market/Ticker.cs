namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// One market snapshot row. Mirrors <c>market_snapshot_latest</c>; the row is written whole or
/// not at all, so every field here except <see cref="Depth"/> is required.
/// </summary>
/// <param name="FundingRate">
/// Fraction of notional per funding interval, positive = longs pay shorts.
/// </param>
/// <param name="Turnover24h">Rolling 24 h turnover in the quote asset, per the venue's definition.</param>
/// <param name="OpenInterest">In units of quantity; notional is OpenInterest × MarkPrice.</param>
/// <param name="OpenInterestAt">OI is a separate, slower call on some venues — hence its own time.</param>
public sealed record Ticker(
    string ExchangeSymbol,
    DateTimeOffset ReceivedAt,
    double LastPrice,
    double BidPrice,
    double AskPrice,
    double BidSize,
    double AskSize,
    double MarkPrice,
    double IndexPrice,
    double FundingRate,
    double Turnover24h,
    double OpenInterest,
    DateTimeOffset OpenInterestAt,
    Depth? Depth);

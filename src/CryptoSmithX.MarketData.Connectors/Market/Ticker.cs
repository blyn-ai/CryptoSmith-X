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
/// <param name="VenueTs">The venue's own clock for this ticker, where it sends one — distinct from
/// <see cref="ReceivedAt"/>, which is when WE observed it (0030). Null where the venue's ticker
/// carries no timestamp of its own.</param>
/// <param name="LastTradeAt">The instant of the trade <see cref="LastPrice"/> reports, where the
/// venue's ticker distinguishes a genuine last-trade price from a proxy. Always null on
/// Hyperliquid: its LastPrice is book mid, not a trade (0030, 0035 §A) — never a missing
/// measurement, a structural fact about what that venue's ticker is.</param>
/// <param name="FundingRatePredicted">The venue's own forecast for the NEXT funding period, where
/// it publishes one — distinct from <see cref="FundingRate"/>, which is the currently-accruing
/// rate. Null where the venue doesn't forecast it.</param>
/// <param name="NextFundingAt">When the next funding payment settles, where the venue publishes
/// it.</param>
/// <param name="Volume24hBase">Rolling 24 h turnover in the BASE asset — independent of
/// <see cref="Turnover24h"/> (quote asset); one is not derivable from the other without a price
/// that isn't constant across the window.</param>
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
    Depth? Depth,
    DateTimeOffset? VenueTs = null,
    DateTimeOffset? LastTradeAt = null,
    double? FundingRatePredicted = null,
    DateTimeOffset? NextFundingAt = null,
    double? Volume24hBase = null);

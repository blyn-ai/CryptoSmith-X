namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// One historical funding payment for an instrument. Venues serve these back in time, so unlike
/// the snapshot's live rate this is a series that can be back-filled and must be kept.
/// </summary>
/// <param name="FundingTime">The payment moment — the interval boundary (UTC).</param>
/// <param name="Rate">
/// Fraction of notional for that interval, positive = longs pay shorts. Same semantics as
/// <see cref="Ticker.FundingRate"/>.
/// </param>
public sealed record FundingRate(
    string ExchangeSymbol,
    DateTimeOffset FundingTime,
    double Rate);

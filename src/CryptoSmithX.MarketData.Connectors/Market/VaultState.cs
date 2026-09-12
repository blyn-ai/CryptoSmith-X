namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// What a vault-backed venue publishes about ONE PAIR that a book-backed one has no equivalent for:
/// open interest by side, the caps it is held under, and the INPUTS to the price-impact function.
///
/// <b>Inputs, not samples.</b> The spread on such a venue is a function of (side, notional, moment),
/// so storing its value at three or four chosen sizes would fix those sizes forever and lose
/// everything between them. From these inputs the cost of ANY notional is reproducible afterwards,
/// which makes the choice of sizes a question for the reader rather than a decision baked into the
/// schema.
///
/// <b>And never into <c>depth_*bps</c>.</b> Those columns are documented as a measurement of a book
/// (0035 §A). This market has no book, and putting a modelled number where a measured one belongs
/// would be the exact confusion the whole surface exists to prevent. Comparing the two at equal
/// notional is a job for the reader, by inverting the function.
/// </summary>
public sealed record VaultPairState(
    string ExchangeSymbol,
    DateTimeOffset At,
    double? OiLongBase,
    double? OiShortBase,
    double? OiLongQuote,
    double? OiShortQuote,
    double? OiMaxQuote,
    double? OiBlockLimit,
    double? DepthAbove1Pct,
    double? DepthBelow1Pct,
    double? LiquidityBuy,
    double? LiquiditySell,
    double? PriceImpactMultiplier,
    double? SkewImpactMultiplier,
    double? SpreadPercent,
    double? DecayedVol,

    /// <summary>Funding for the long and short side, PERCENT PER HOUR — the unit measured in 0051
    /// against the venue's own documented "below 3% a year on RWAs", which only WTI's 2.50%/yr
    /// satisfies under this reading and no asset satisfies under the other.
    ///
    /// Two fields and not one, and not one negated: the counterparty here is the pool, so the sides
    /// are independent. ETH at the time of writing pays 0.00109998 long against 0.00114364
    /// received short.</summary>
    double? FundingLongPHour = null,
    double? FundingShortPHour = null,

    /// <summary>The borrow half of the venue's "net rate" — paid to liquidity providers rather than
    /// exchanged with traders. Kept apart from funding in storage because they are different
    /// quantities; a reader who wants the net adds them, having both.</summary>
    double? MarginFeeLongPHour = null,
    double? MarginFeeShortPHour = null);

/// <summary>
/// The liquidity pool itself — an observation about the VENUE, not about any instrument on it.
///
/// On a vault-backed venue this pool is the counterparty of every trade, which makes it the nearest
/// thing the venue has to a book's depth: how much can be taken, at what share price, and how much
/// of the pool is already committed. It has no instrument because it genuinely has none, which is
/// why it is keyed on the segment instead.
/// </summary>
public sealed record VaultState(
    DateTimeOffset At,
    double? TotalAssetsQuote,
    double? TotalSupplyShares,
    double? SharePriceQuote,
    double? UtilizationRatio,
    double? DepositCapQuote,
    double? WithdrawThresholdQuote);

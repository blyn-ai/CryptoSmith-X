namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// One market snapshot row. Mirrors <c>market_snapshot_latest</c>.
///
/// <b>Every figure is nullable, and that is the whole point.</b> This record used to require ten
/// of them, because the columns behind it were NOT NULL and a row was written whole or not at all;
/// an adapter that had not been given a number signalled it as <c>NaN</c>, and
/// <c>SnapshotCollector</c> dropped the entire observation. That cost the honest venues nothing —
/// all four publish everything — and cost every venue that does NOT the ability to be recorded at
/// all. A spot market has no funding and no open interest by nature; a vault-backed perp has no
/// bid and no ask by nature. Neither could be written, so neither could be listed.
///
/// Migration 0030 lifted NOT NULL from all eleven columns and said in its own §A that the lifting
/// changes nothing until the collectors are rewritten. This is that rewrite. NULL here means
/// exactly what it means in the column: <b>not measured</b> — either the venue does not publish it
/// or we did not get it this pass. It never means zero, and a zero must never be written in its
/// place: a zero is a measurement, and inventing one is the single thing this system must not do.
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
    double? LastPrice,
    double? BidPrice,
    double? AskPrice,
    double? BidSize,
    double? AskSize,
    double? MarkPrice,
    double? IndexPrice,
    double? FundingRate,
    double? Turnover24h,
    double? OpenInterest,
    DateTimeOffset? OpenInterestAt,
    Depth? Depth,
    DateTimeOffset? VenueTs = null,
    DateTimeOffset? LastTradeAt = null,
    double? FundingRatePredicted = null,
    DateTimeOffset? NextFundingAt = null,
    double? Volume24hBase = null,

    /// <summary>Open interest in the QUOTE asset, as the venue publishes it — never our own
    /// open_interest × price. The pair to <see cref="OpenInterest"/>, which stays in base units;
    /// a venue that publishes both (Avantis does) fills both and derives neither.</summary>
    double? OiQuote = null,

    /// <summary>Whether the instrument is trading at this observation, where the venue says so.
    /// Null is "this venue publishes no such flag", which is every venue wired before Avantis —
    /// and it is never inferred from a calendar we do not hold.</summary>
    bool? MarketOpen = null);

namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// One executed trade as the venue published it. Mirrors <c>trade</c> (0032): the identity is the
/// venue's own, the clock is the venue's own, and the quantity is in the instrument's units — the
/// row is tied to an instrument id, so the unit is not carried per row.
/// </summary>
/// <param name="VenueUid">The venue's trade identifier verbatim — a number on Binance
/// (<c>aggTrade.a</c>) and Hyperliquid (<c>tid</c>), a uuid on WEEX and Kraken. Stored as text and
/// never parsed, per the column's own comment.</param>
/// <param name="Seq">The venue's sequence number where it publishes one (Kraken). Null elsewhere:
/// a fabricated counter would look like the venue's own ordering and is not.</param>
/// <param name="TakerSide">Which side crossed the spread — 'buy' or 'sell'. Derived per venue from
/// whatever the venue states (Binance's maker flag, Kraken's <c>side</c>, Hyperliquid's A/B).</param>
/// <param name="TradeType">'fill', 'liquidation', 'partial_liquidation', 'termination' or 'block'
/// where the venue marks it (Kraken's <c>type</c>, Binance's forceOrder stream). NULL where the
/// venue does not distinguish — never defaulted to 'fill', per the column's own comment.</param>
public sealed record TradeEvent(
    string ExchangeSymbol,
    DateTimeOffset EventTime,
    string VenueUid,
    long? Seq,
    double Price,
    double Qty,
    string TakerSide,
    string? TradeType);

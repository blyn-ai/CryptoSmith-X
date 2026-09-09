namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// One bucket of open-interest history. Mirrors <c>open_interest_history</c> (0032).
/// </summary>
/// <param name="Open">Only where the venue aggregates OHLC itself (Kraken's analytics series does;
/// Binance's openInterestHist publishes a single point per bucket). NULL is "the venue does not
/// aggregate it", not "not measured" — <paramref name="Close"/> is then the whole bucket.</param>
/// <param name="Quote">OI in quote notional where the venue publishes it ready-made (Binance's
/// <c>sumOpenInterestValue</c>). Never our own oi × mark: the column's comment is explicit that a
/// reader with both numbers can do that multiplication itself.</param>
/// <param name="Source">'analytics' where the venue has a history endpoint of its own, 'rest' where
/// this is our own periodic observation of the venue's CURRENT open interest bucketed onto a fixed
/// grid (WEEX and Hyperliquid publish no history at all).</param>
public sealed record OpenInterestBucket(
    string ExchangeSymbol,
    int IntervalSeconds,
    DateTimeOffset BucketTime,
    double? Open,
    double? High,
    double? Low,
    double Close,
    double? Quote,
    string Source);

/// <summary>
/// One closed mark- or index-price bar. Mirrors <c>market_price_candle</c> (0032): no volume, since
/// neither series trades, and only closed bars are ever produced.
/// </summary>
/// <param name="Series">'mark' or 'index' — two different series of the same instrument, not two
/// spellings of one value.</param>
public sealed record PriceCandle(
    string ExchangeSymbol,
    string Series,
    DateTimeOffset OpenTime,
    double Open,
    double High,
    double Low,
    double Close);

/// <summary>
/// One bucket of aggregated liquidation volume. Mirrors <c>liquidation_volume_history</c> (0032),
/// which is built from a venue's own aggregate rather than from our <c>trade</c> rows — the table's
/// comment is explicit about that, and it is why this carries the venue's unit per row.
/// </summary>
/// <param name="VolumeUnit">What the venue's number counts: 'base' for a quantity in instrument
/// units, 'quote' for a notional. Not derivable from the instrument, which is why the schema keeps
/// it on the row.</param>
public sealed record LiquidationBucket(
    string ExchangeSymbol,
    int IntervalSeconds,
    DateTimeOffset BucketTime,
    double Volume,
    string VolumeUnit);

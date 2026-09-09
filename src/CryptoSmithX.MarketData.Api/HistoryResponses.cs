namespace CryptoSmithX.MarketData.Api;

/// <summary>
/// The shapes the historical endpoints answer with. Declared as records rather than the anonymous
/// objects the original six endpoints use so the OpenAPI document carries a real schema for each —
/// a reference page that lists an endpoint and says only "200" describes nothing.
///
/// Every one of these is a raw stored fact, a venue timestamp, or a unit conversion the schema
/// itself prescribes (quantity into notional through the instrument's own contract multiplier).
/// Nothing here is an indicator, an average, a change, a ranking or a score: those belong to
/// whoever is reading, who knows what window and what definition they meant.
/// </summary>
public static class HistoryResponses
{
    /// <summary>The request as the server understood it, echoed so a caller can tell a narrowed
    /// answer from a narrowed question.</summary>
    public sealed record Query(
        string Exchange,
        IReadOnlyList<string> Symbols,
        DateTimeOffset From,
        DateTimeOffset To,
        int Limit);

    /// <summary>
    /// <paramref name="NextCursor"/> is null when the page was the last one. It is present whenever
    /// the page filled to <c>limit</c>, even if the next page turns out to be empty — the server
    /// cannot know that without fetching it, and claiming the end early is the one answer a paging
    /// caller cannot recover from.
    /// </summary>
    public sealed record Page<T>(
        DateTimeOffset AsOf,
        string Exchange,
        Query Query,
        IReadOnlyList<T> Items,
        string? NextCursor,
        IReadOnlyList<string> Warnings);

    public sealed record TickerRow(
        DateTimeOffset Utc,
        string Symbol,
        double? LastPrice,
        double? BidPrice,
        double? AskPrice,
        double? BidSize,
        double? AskSize,
        double? MarkPrice,
        double? IndexPrice,
        double? Turnover24h);

    /// <param name="NextFundingAt">Null on every historical row: the venue's stated next-payment
    /// instant is carried on the live ticker, not on the settled history, and deriving it from
    /// funding_time plus the interval would be our arithmetic presented as the venue's statement.</param>
    public sealed record FundingRow(
        DateTimeOffset Utc,
        string Symbol,
        double FundingRate,
        short? FundingIntervalHours,
        DateTimeOffset? NextFundingAt);

    /// <param name="OpenInterestNotional">The venue's own quote-denominated figure where it
    /// publishes one, null where it does not — Kraken's analytics series carries contracts only.
    /// Multiplying by a mark price from another row would be a different measurement wearing this
    /// one's name.</param>
    public sealed record OpenInterestRow(
        DateTimeOffset Utc,
        string Symbol,
        int IntervalSeconds,
        decimal OpenInterest,
        decimal? OpenInterestNotional);

    /// <param name="DepthRef">The mid the bands were measured from. Without it the bands are not
    /// reproducible: bps is a distance from a reference, not a level.</param>
    public sealed record DepthRow(
        DateTimeOffset Utc,
        string Symbol,
        IReadOnlyDictionary<string, double?> BidDepth,
        IReadOnlyDictionary<string, double?> AskDepth,
        double? DepthRef);

    /// <param name="Side">The taker side the venue reported, <c>buy</c> or <c>sell</c>. Not mapped
    /// to long/short: which position was closed is an interpretation of this fact, not the fact.</param>
    public sealed record TradeRow(
        DateTimeOffset Utc,
        string Symbol,
        string? TradeId,
        string Side,
        decimal Price,
        decimal Quantity,
        decimal Notional,
        string? TradeType);

    /// <summary>Per-event liquidations, which exist only where the venue marks them on its own tape.
    /// Where it instead publishes an aggregate, that is a different dataset with no price and no
    /// side, and it is served by its own endpoint rather than reshaped to look like this one.</summary>
    public sealed record LiquidationRow(
        DateTimeOffset Utc,
        string Symbol,
        string Side,
        decimal Price,
        decimal Quantity,
        decimal Notional,
        string? TradeType);

    /// <param name="Source">'analytics' where the venue computed the total itself, 'ws' where it is
    /// our sum over the venue's own liquidation events — a bucket we assembled starts when we started
    /// listening, which a venue-computed total does not.</param>
    public sealed record LiquidationVolumeRow(
        DateTimeOffset Utc,
        string Symbol,
        int IntervalSeconds,
        decimal Volume,
        string VolumeUnit,
        string? Source);

    public sealed record BookLevel(decimal Price, decimal Quantity, int? Orders);

    /// <param name="IsSnapshot">True where the frame is the venue's own full snapshot rather than a
    /// state we rebuilt from deltas.</param>
    public sealed record BookSnapshotRow(
        DateTimeOffset Utc,
        string Symbol,
        long Seq,
        bool IsSnapshot,
        int Levels,
        IReadOnlyList<BookLevel> Bids,
        IReadOnlyList<BookLevel> Asks);

    /// <param name="UpdatedAt">Kept because it was in this endpoint's original response and callers
    /// may read it; null on mark and index series, which are written by a different collector.</param>
    public sealed record CandleRow(
        DateTimeOffset OpenTime,
        DateTimeOffset CloseTime,
        double Open,
        double High,
        double Low,
        double Close,
        double? Volume,
        double? VolumeQuote,
        int? TradeCount,
        short? BarCount,
        DateTimeOffset? UpdatedAt);

    /// <summary>
    /// Candles keep their own envelope: one series per symbol rather than a flat list, because a
    /// caller charting two instruments wants them apart, and interleaving them by time would make
    /// every consumer group them again.
    /// </summary>
    public sealed record CandlePage(
        DateTimeOffset AsOf,
        string Exchange,
        int TimeframeMinutes,
        string PriceType,
        DateTimeOffset? From,
        DateTimeOffset? To,
        IReadOnlyDictionary<string, IReadOnlyList<CandleRow>> Series,
        string? NextCursor,
        IReadOnlyList<string> Warnings,
        // Legacy fields, present only when the caller used the singular `symbol`. They are the
        // original response of this endpoint and are kept verbatim so existing callers are
        // untouched; new callers should read `series`.
        string? Symbol = null,
        int? Timeframe = null,
        IReadOnlyList<CandleRow>? Candles = null);
}

using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Dydx;

/// <summary><c>/v4/perpetualMarkets</c> — the whole venue, keyed by ticker.</summary>
internal sealed record DydxMarkets(
    [property: JsonPropertyName("markets")] IReadOnlyDictionary<string, DydxMarket>? Markets);

/// <summary>
/// One market as the indexer publishes it. Every number is a string.
///
/// <b>Units, measured 2026-09-13 on BTC-USD:</b> <c>openInterest</c> is the BASE asset (200.39 BTC);
/// <c>volume24H</c> is USD, confirmed against the sum of the venue's own 24 hourly bars (750 651
/// against 789 835, the difference being the hour still forming); <c>nextFundingRate</c> is a
/// per-HOUR rate, the period this venue settles on. There is no last price, no bid and no ask on
/// this route — those live on the book and the tape.
/// </summary>
internal sealed record DydxMarket(
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("oraclePrice")] string? OraclePrice,
    [property: JsonPropertyName("volume24H")] string? Volume24H,
    [property: JsonPropertyName("trades24H")] int? Trades24H,
    [property: JsonPropertyName("nextFundingRate")] string? NextFundingRate,
    [property: JsonPropertyName("openInterest")] string? OpenInterest,
    [property: JsonPropertyName("tickSize")] string? TickSize,
    [property: JsonPropertyName("stepSize")] string? StepSize,
    [property: JsonPropertyName("marketType")] string? MarketType);

internal sealed record DydxBook(
    [property: JsonPropertyName("bids")] IReadOnlyList<DydxLevel>? Bids,
    [property: JsonPropertyName("asks")] IReadOnlyList<DydxLevel>? Asks);

internal sealed record DydxLevel(
    [property: JsonPropertyName("price")] string? Price,
    [property: JsonPropertyName("size")] string? Size);

internal sealed record DydxTrades(
    [property: JsonPropertyName("trades")] IReadOnlyList<DydxTrade>? Trades);

/// <param name="Side">The TAKER's side, BUY or SELL.</param>
/// <param name="Type">LIMIT, LIQUIDATED or DELEVERAGED. The tape marks a liquidation on the print
/// itself, which is what lets this venue report liquidations without a separate feed.</param>
internal sealed record DydxTrade(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("side")] string? Side,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("price")] string? Price,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("createdAtHeight")] string? CreatedAtHeight);

internal sealed record DydxCandles(
    [property: JsonPropertyName("candles")] IReadOnlyList<DydxCandle>? Candles);

/// <param name="StartingOpenInterest">Open interest, in the base asset, at the instant this bar
/// OPENED — which makes it the closing open interest of the bar before it.</param>
internal sealed record DydxCandle(
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("open")] string? Open,
    [property: JsonPropertyName("high")] string? High,
    [property: JsonPropertyName("low")] string? Low,
    [property: JsonPropertyName("close")] string? Close,
    [property: JsonPropertyName("baseTokenVolume")] string? BaseTokenVolume,
    [property: JsonPropertyName("usdVolume")] string? UsdVolume,
    [property: JsonPropertyName("trades")] int? Trades,
    [property: JsonPropertyName("startingOpenInterest")] string? StartingOpenInterest);

internal sealed record DydxFundingHistory(
    [property: JsonPropertyName("historicalFunding")] IReadOnlyList<DydxFundingRow>? HistoricalFunding);

internal sealed record DydxFundingRow(
    [property: JsonPropertyName("rate")] string? Rate,
    [property: JsonPropertyName("effectiveAt")] DateTimeOffset? EffectiveAt);

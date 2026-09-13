using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// The wire shapes of Binance SPOT's <c>/api/v3</c>.
///
/// Separate from <see cref="BinanceSymbol"/> and its neighbours on purpose: the futures rows carry
/// <c>contractType</c>, <c>onboardDate</c> and a funding interval, and the spot rows carry none of
/// those and add <c>isSpotTradingAllowed</c>. Reusing one record for both would mean a type whose
/// half the fields are null depending on which venue answered, and every reader would have to know
/// which half.
/// </summary>
internal sealed record BinanceSpotExchangeInfo(
    [property: JsonPropertyName("symbols")] IReadOnlyList<BinanceSpotSymbol>? Symbols);

/// <param name="IsSpotTradingAllowed">The venue's own statement that this pair can be traded on
/// spot at all. False on 40 of the 1 086 USD-family rows, every one of them also BREAK — but the
/// flag is what says so, and a pair that cannot be traded on spot does not belong in a spot
/// segment whatever its status happens to read.</param>
internal sealed record BinanceSpotSymbol(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("baseAsset")] string? BaseAsset,
    [property: JsonPropertyName("quoteAsset")] string? QuoteAsset,
    [property: JsonPropertyName("isSpotTradingAllowed")] bool? IsSpotTradingAllowed,
    [property: JsonPropertyName("filters")] IReadOnlyList<BinanceSpotFilter>? Filters)
{
    /// <summary>The row as it arrived, for the audit column. Filled by the client, not by the
    /// serializer.</summary>
    public string RawJson { get; init; } = "";
}

/// <summary>One entry of <c>filters</c>. A tagged union on the wire, so every field but the tag is
/// optional and which ones are present depends on <see cref="FilterType"/>.</summary>
internal sealed record BinanceSpotFilter(
    [property: JsonPropertyName("filterType")] string FilterType,
    [property: JsonPropertyName("tickSize")] string? TickSize,
    [property: JsonPropertyName("stepSize")] string? StepSize,
    [property: JsonPropertyName("minQty")] string? MinQty,
    [property: JsonPropertyName("minNotional")] string? MinNotional);

/// <summary>
/// One row of <c>/api/v3/ticker/24hr</c>.
///
/// <b>This one row closes the whole snapshot.</b> It carries the rolling-window figures AND the
/// live top of book — <c>bidPrice</c>, <c>bidQty</c>, <c>askPrice</c>, <c>askQty</c> — which is why
/// this adapter does not also call <c>ticker/bookTicker</c>. Measured 2026-09-13: 3 701 rows,
/// 1 850 KB, 0.54 s, weight 80; bookTicker is 416 KB at weight 4 but carries no turnover, so asking
/// for both would cost more and buy a second clock over the same two numbers.
/// </summary>
internal sealed record BinanceSpotTicker(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("lastPrice")] string? LastPrice,
    [property: JsonPropertyName("bidPrice")] string? BidPrice,
    [property: JsonPropertyName("bidQty")] string? BidQty,
    [property: JsonPropertyName("askPrice")] string? AskPrice,
    [property: JsonPropertyName("askQty")] string? AskQty,
    /// <summary>Base-asset volume over the window.</summary>
    [property: JsonPropertyName("volume")] string? Volume,
    /// <summary>The same volume in the quote asset — turnover, as the venue computes it.</summary>
    [property: JsonPropertyName("quoteVolume")] string? QuoteVolume,
    /// <summary>The window's closing instant, which is the venue's own clock on this frame.</summary>
    [property: JsonPropertyName("closeTime")] long? CloseTime);

/// <summary><c>/api/v3/depth</c>. <b>No timestamp of any kind</b> — not in the body and not in a
/// header — so the instant a band is stamped with is our own, and that is said where it is
/// used.</summary>
internal sealed record BinanceSpotDepth(
    [property: JsonPropertyName("lastUpdateId")] long? LastUpdateId,
    [property: JsonPropertyName("bids")] IReadOnlyList<string[]>? Bids,
    [property: JsonPropertyName("asks")] IReadOnlyList<string[]>? Asks);

/// <param name="BuyerIsMaker">TRUE means the buyer was resting and the TAKER SOLD. The field names
/// the wrong side of the trade for the column it feeds, and reading it as "was a buy" inverts every
/// print on the venue.</param>
internal sealed record BinanceAggTrade(
    [property: JsonPropertyName("a")] long? Id,
    [property: JsonPropertyName("p")] string? Price,
    [property: JsonPropertyName("q")] string? Qty,
    [property: JsonPropertyName("T")] long? Timestamp,
    [property: JsonPropertyName("m")] bool? BuyerIsMaker);

using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Bitget;

/// <summary>
/// Bitget's v2 envelope. Like Bybit, failure arrives INSIDE a 200 — <c>code</c> is the string
/// "00000" on success and something else on refusal — so the status line alone is not an answer.
/// </summary>
internal sealed record BitgetEnvelope<T>(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("msg")] string? Msg,
    [property: JsonPropertyName("data")] T? Data);

/// <summary>
/// One row of <c>/api/v2/mix/market/tickers</c>. Measured live: 787 rows, 396 KB, 0.40 s, and not
/// one empty field across them.
/// </summary>
/// <param name="HoldingAmount">Open interest in BASE units. The venue publishes no quote-notional
/// counterpart on this route, so <c>oi_quote</c> stays null rather than becoming our own
/// holdingAmount × mark — the column exists to hold the venue's own figure or nothing.</param>
/// <param name="Ts">The venue's own clock for this row, inside the frame. Written to venue_ts so a
/// figure's age is the venue's, not the instant we happened to write the row.</param>
internal sealed record BitgetTicker(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("lastPr")] string? LastPr,
    [property: JsonPropertyName("bidPr")] string? BidPr,
    [property: JsonPropertyName("bidSz")] string? BidSz,
    [property: JsonPropertyName("askPr")] string? AskPr,
    [property: JsonPropertyName("askSz")] string? AskSz,
    [property: JsonPropertyName("markPrice")] string? MarkPrice,
    [property: JsonPropertyName("indexPrice")] string? IndexPrice,
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("holdingAmount")] string? HoldingAmount,
    [property: JsonPropertyName("baseVolume")] string? BaseVolume,
    [property: JsonPropertyName("quoteVolume")] string? QuoteVolume,
    [property: JsonPropertyName("ts")] string? Ts);

/// <param name="FundInterval">HOURS, as a string ("8"). Unlike Bybit this venue has one spelling
/// and one unit, so there is nothing to confuse it with.</param>
/// <param name="SymbolType">'perpetual' or 'delivery' — the venue's own split, which is what keeps
/// a dated contract out of a segment whose kind is 'perp'.</param>
internal sealed record BitgetContract(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("baseCoin")] string? BaseCoin,
    [property: JsonPropertyName("quoteCoin")] string? QuoteCoin,
    [property: JsonPropertyName("symbolType")] string? SymbolType,
    [property: JsonPropertyName("symbolStatus")] string? SymbolStatus,
    [property: JsonPropertyName("minTradeNum")] string? MinTradeNum,
    [property: JsonPropertyName("minTradeUSDT")] string? MinTradeUsdt,
    [property: JsonPropertyName("sizeMultiplier")] string? SizeMultiplier,
    [property: JsonPropertyName("pricePlace")] string? PricePlace,
    [property: JsonPropertyName("priceEndStep")] string? PriceEndStep,
    [property: JsonPropertyName("fundInterval")] string? FundInterval,
    [property: JsonPropertyName("launchTime")] string? LaunchTime);

internal sealed record BitgetFundingRow(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("fundingTime")] string? FundingTime);

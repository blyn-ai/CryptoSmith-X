using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Synthetix;

/// <summary>Every answer from <c>POST /v1/info</c> comes in this envelope; a refusal says
/// <c>status: "error"</c> with a code, and HTTP 429 carries <c>RATE_LIMIT_EXCEEDED</c>.</summary>
internal sealed record SynthetixEnvelope<T>(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("response")] T? Response,
    [property: JsonPropertyName("error")] SynthetixError? Error);

internal sealed record SynthetixError(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);

/// <param name="ContractSize">1 on every market captured — quantities are base-asset units.</param>
internal sealed record SynthetixMarket(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("baseAsset")] string? BaseAsset,
    [property: JsonPropertyName("quoteAsset")] string? QuoteAsset,
    [property: JsonPropertyName("isOpen")] bool? IsOpen,
    [property: JsonPropertyName("isCloseOnly")] bool? IsCloseOnly,
    [property: JsonPropertyName("priceIncrement")] string? PriceIncrement,
    [property: JsonPropertyName("minOrderSize")] string? MinOrderSize,
    [property: JsonPropertyName("orderSizeIncrement")] string? OrderSizeIncrement,
    [property: JsonPropertyName("contractSize")] decimal? ContractSize,
    [property: JsonPropertyName("minNotionalValue")] string? MinNotionalValue);

/// <summary>
/// One market in <c>getMarketPrices</c>, which answers for the whole venue keyed by symbol.
///
/// <b>Units, measured 2026-09-13 on BTC-USDT:</b> <c>volume24h</c> and <c>openInterest</c> are the BASE
/// asset (4.30 and 0.459 BTC) and <c>quoteVolume24h</c> is USDT (331 781, which is 4.30 at the day's
/// price). <c>fundingRate</c> here is the ESTIMATE for the period now accruing — the same number
/// <c>getFundingRate</c> calls <c>estimatedFundingRate</c> — and not the rate last settled.
/// <c>timestamp</c> is the venue's own clock.
/// </summary>
internal sealed record SynthetixPrice(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("bestBid")] string? BestBid,
    [property: JsonPropertyName("bestAsk")] string? BestAsk,
    [property: JsonPropertyName("markPrice")] string? MarkPrice,
    [property: JsonPropertyName("indexPrice")] string? IndexPrice,
    [property: JsonPropertyName("lastPrice")] string? LastPrice,
    [property: JsonPropertyName("volume24h")] string? Volume24h,
    [property: JsonPropertyName("quoteVolume24h")] string? QuoteVolume24h,
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("openInterest")] string? OpenInterest,
    [property: JsonPropertyName("timestamp")] long? Timestamp);

internal sealed record SynthetixFundingRate(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("estimatedFundingRate")] string? EstimatedFundingRate,
    [property: JsonPropertyName("lastSettlementRate")] string? LastSettlementRate,
    [property: JsonPropertyName("lastSettlementTime")] long? LastSettlementTime,
    [property: JsonPropertyName("nextFundingTime")] long? NextFundingTime,
    [property: JsonPropertyName("fundingInterval")] long? FundingIntervalMs);

/// <summary>Levels as <c>[price, quantity]</c> string pairs, best first.</summary>
internal sealed record SynthetixBook(
    [property: JsonPropertyName("bids")] IReadOnlyList<string[]>? Bids,
    [property: JsonPropertyName("asks")] IReadOnlyList<string[]>? Asks);

internal sealed record SynthetixTrades(
    [property: JsonPropertyName("trades")] IReadOnlyList<SynthetixTrade>? Trades);

/// <param name="IsMaker">Whose side <see cref="Side"/> names. False on every public print captured,
/// so the side is the taker's; read defensively all the same.</param>
internal sealed record SynthetixTrade(
    [property: JsonPropertyName("tradeId")] string? TradeId,
    [property: JsonPropertyName("side")] string? Side,
    [property: JsonPropertyName("price")] string? Price,
    [property: JsonPropertyName("quantity")] string? Quantity,
    [property: JsonPropertyName("timestamp")] long? Timestamp,
    [property: JsonPropertyName("isMaker")] bool? IsMaker);

internal sealed record SynthetixCandles(
    [property: JsonPropertyName("candles")] IReadOnlyList<SynthetixCandle>? Candles);

internal sealed record SynthetixCandle(
    [property: JsonPropertyName("openTime")] long OpenTime,
    [property: JsonPropertyName("openPrice")] string? OpenPrice,
    [property: JsonPropertyName("highPrice")] string? HighPrice,
    [property: JsonPropertyName("lowPrice")] string? LowPrice,
    [property: JsonPropertyName("closePrice")] string? ClosePrice,
    [property: JsonPropertyName("volume")] string? Volume,
    [property: JsonPropertyName("quoteVolume")] string? QuoteVolume,
    [property: JsonPropertyName("tradeCount")] int? TradeCount);

internal sealed record SynthetixFundingHistory(
    [property: JsonPropertyName("fundingRates")] IReadOnlyList<SynthetixFundingRow>? FundingRates);

internal sealed record SynthetixFundingRow(
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("fundingTime")] long? FundingTime);

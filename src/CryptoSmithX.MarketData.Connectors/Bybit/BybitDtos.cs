using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Bybit;

/// <summary>
/// Bybit wraps every v5 response in the same envelope and signals failure INSIDE a 200: a bad
/// symbol comes back HTTP 200 with <c>retCode</c> 10001. Checking the status code alone would read
/// that as success and then map an empty list, which is how a venue outage turns into "this
/// instrument went quiet" — see <see cref="BybitClient"/>, which refuses on a non-zero code.
/// </summary>
internal sealed record BybitEnvelope<T>(
    [property: JsonPropertyName("retCode")] int RetCode,
    [property: JsonPropertyName("retMsg")] string? RetMsg,
    [property: JsonPropertyName("result")] T? Result);

internal sealed record BybitList<T>(
    [property: JsonPropertyName("list")] IReadOnlyList<T>? List,
    [property: JsonPropertyName("nextPageCursor")] string? NextPageCursor);

/// <summary>
/// One row of <c>/v5/market/tickers?category=linear</c> — the call that closes every snapshot column
/// this venue can fill, in one request for the whole segment.
/// </summary>
/// <param name="FundingIntervalHour">HOURS, and the trap on this venue: the instruments endpoint
/// publishes <c>fundingInterval</c> in MINUTES (240) while this one publishes
/// <c>fundingIntervalHour</c> in hours (8). Same venue, same concept, two units and two spellings —
/// so each is read only where it is named, and neither is converted into the other's field.</param>
internal sealed record BybitTicker(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("lastPrice")] string? LastPrice,
    [property: JsonPropertyName("bid1Price")] string? Bid1Price,
    [property: JsonPropertyName("bid1Size")] string? Bid1Size,
    [property: JsonPropertyName("ask1Price")] string? Ask1Price,
    [property: JsonPropertyName("ask1Size")] string? Ask1Size,
    [property: JsonPropertyName("markPrice")] string? MarkPrice,
    [property: JsonPropertyName("indexPrice")] string? IndexPrice,
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("fundingIntervalHour")] int? FundingIntervalHour,
    [property: JsonPropertyName("nextFundingTime")] string? NextFundingTime,
    [property: JsonPropertyName("openInterest")] string? OpenInterest,
    [property: JsonPropertyName("openInterestValue")] string? OpenInterestValue,
    [property: JsonPropertyName("turnover24h")] string? Turnover24h,
    [property: JsonPropertyName("volume24h")] string? Volume24h);

internal sealed record BybitInstrument(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("contractType")] string? ContractType,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("baseCoin")] string? BaseCoin,
    [property: JsonPropertyName("quoteCoin")] string? QuoteCoin,
    [property: JsonPropertyName("launchTime")] string? LaunchTime,
    [property: JsonPropertyName("fundingInterval")] int? FundingIntervalMinutes,
    [property: JsonPropertyName("priceFilter")] BybitPriceFilter? PriceFilter,
    [property: JsonPropertyName("lotSizeFilter")] BybitLotFilter? LotSizeFilter);

internal sealed record BybitPriceFilter(
    [property: JsonPropertyName("tickSize")] string? TickSize);

internal sealed record BybitLotFilter(
    [property: JsonPropertyName("qtyStep")] string? QtyStep,
    [property: JsonPropertyName("minOrderQty")] string? MinOrderQty,
    [property: JsonPropertyName("minNotionalValue")] string? MinNotionalValue);

internal sealed record BybitFundingRow(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("fundingRateTimestamp")] string? FundingRateTimestamp);

/// <summary>
/// <c>openInterest</c> is the whole contract's open interest in BASE units; <c>singleOpenInterest</c>
/// is the venue's one-sided figure and is deliberately not read — two numbers for one concept, and
/// only one of them is what <c>open_interest_history</c> means.
/// </summary>
internal sealed record BybitOpenInterestRow(
    [property: JsonPropertyName("openInterest")] string? OpenInterest,
    [property: JsonPropertyName("timestamp")] string? Timestamp);

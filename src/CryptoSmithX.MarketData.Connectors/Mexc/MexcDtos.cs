using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Mexc;

/// <summary>
/// MEXC's contract envelope — and the one place in this codebase where a venue's refusal MUST be
/// translated by hand.
///
/// The throttle arrives as <b>HTTP 200</b> with <c>{"success":false,"code":510,"message":"Requests
/// are too frequent"}</c>. <c>VenueGate.Penalize()</c> is documented as the reaction to a 429 and
/// would therefore never fire on this venue; <see cref="MexcClient"/> calls it on 510 itself.
/// Measured: 120 requests offered at the DOCUMENTED 10/s gave
/// <c>{(200, code 0): 60, (200, code 510): 60}</c> — half refused at the published rate — while the
/// same endpoint at about 2.5/s went 20 for 20 clean.
/// </summary>
internal sealed record MexcEnvelope<T>(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("data")] T? Data);

/// <summary>
/// One row of <c>/api/v1/contract/ticker</c>. Every figure is a JSON NUMBER here, not a string —
/// the opposite of the other five venues in this batch, and the reason these fields are typed
/// <c>double?</c> rather than parsed out of text.
/// </summary>
/// <param name="HoldVol">Open interest in CONTRACTS. One contract is <c>contractSize</c> of the base
/// asset (0.0001 BTC on BTC_USDT), so 448 807 331 is 44 880.73 BTC.</param>
/// <param name="FairPrice">This venue's name for the mark price.</param>
/// <param name="Volume24">Contracts; <paramref name="Amount24"/> is the same 24 hours in quote.
/// Cross-checked: 245 092 560 × 0.0001 is 24 509 BTC against amount24/last of 24 537 — the gap is
/// a day's price drift, which is what tells you the two fields really are the same trade flow.</param>
internal sealed record MexcTicker(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("lastPrice")] double? LastPrice,
    [property: JsonPropertyName("bid1")] double? Bid1,
    [property: JsonPropertyName("ask1")] double? Ask1,
    [property: JsonPropertyName("indexPrice")] double? IndexPrice,
    [property: JsonPropertyName("fairPrice")] double? FairPrice,
    [property: JsonPropertyName("fundingRate")] double? FundingRate,
    [property: JsonPropertyName("holdVol")] double? HoldVol,
    [property: JsonPropertyName("volume24")] double? Volume24,
    [property: JsonPropertyName("amount24")] double? Amount24,
    [property: JsonPropertyName("timestamp")] long? Timestamp);

/// <param name="ApiAllowed">False on 44 of 1 192 contracts. The venue is saying its own API will not
/// serve that symbol, so discovery keeps it out rather than every per-symbol collector discovering
/// it again one failure at a time.</param>
/// <param name="ContractSize">Base-asset units per contract. This venue quotes sizes in contracts,
/// like Gate and OKX.</param>
internal sealed record MexcContract(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("baseCoin")] string? BaseCoin,
    [property: JsonPropertyName("quoteCoin")] string? QuoteCoin,
    [property: JsonPropertyName("contractSize")] double? ContractSize,
    [property: JsonPropertyName("priceUnit")] double? PriceUnit,
    [property: JsonPropertyName("volUnit")] double? VolUnit,
    [property: JsonPropertyName("minVol")] double? MinVol,
    [property: JsonPropertyName("state")] int? State,
    [property: JsonPropertyName("apiAllowed")] bool? ApiAllowed,
    [property: JsonPropertyName("createTime")] long? CreateTime);

/// <param name="CollectCycle">HOURS. Published only on the funding route — <c>contract/detail</c>
/// carries a <c>fundingInterval</c> field that is null on all 1 192 contracts — which is why the
/// discovery pass reads this bulk call rather than leaving the column empty.</param>
internal sealed record MexcFundingRow(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("fundingRate")] double? FundingRate,
    [property: JsonPropertyName("collectCycle")] int? CollectCycle,
    [property: JsonPropertyName("nextSettleTime")] long? NextSettleTime);

/// <summary>
/// One page of <c>contract/funding_rate/history</c>. The envelope's <c>data</c> is an OBJECT here
/// rather than the array every other route answers with, and the rows live one level down in
/// <c>resultList</c> — which is why this route needs its own shape instead of reusing
/// <see cref="MexcFundingRow"/> directly.
/// </summary>
internal sealed record MexcFundingPage(
    [property: JsonPropertyName("pageSize")] int? PageSize,
    [property: JsonPropertyName("totalCount")] int? TotalCount,
    [property: JsonPropertyName("totalPage")] int? TotalPage,
    [property: JsonPropertyName("currentPage")] int? CurrentPage,
    [property: JsonPropertyName("resultList")] IReadOnlyList<MexcSettledFunding>? ResultList);

/// <summary>A funding payment that HAS SETTLED — <c>settleTime</c> is the moment it was charged,
/// not the next one due, which is the whole difference from <see cref="MexcFundingRow"/>.</summary>
internal sealed record MexcSettledFunding(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("fundingRate")] double? FundingRate,
    [property: JsonPropertyName("settleTime")] long? SettleTime,
    [property: JsonPropertyName("collectCycle")] int? CollectCycle);

internal sealed record MexcKline(
    [property: JsonPropertyName("time")] IReadOnlyList<long>? Time,
    [property: JsonPropertyName("open")] IReadOnlyList<double>? Open,
    [property: JsonPropertyName("high")] IReadOnlyList<double>? High,
    [property: JsonPropertyName("low")] IReadOnlyList<double>? Low,
    [property: JsonPropertyName("close")] IReadOnlyList<double>? Close,
    [property: JsonPropertyName("vol")] IReadOnlyList<double>? Vol,
    [property: JsonPropertyName("amount")] IReadOnlyList<double>? Amount);

/// <summary>The book. Levels are [price, volume, orderCount] arrays of JSON numbers, and the volume
/// is in CONTRACTS.</summary>
internal sealed record MexcDepth(
    [property: JsonPropertyName("bids")] IReadOnlyList<double[]>? Bids,
    [property: JsonPropertyName("asks")] IReadOnlyList<double[]>? Asks,
    [property: JsonPropertyName("timestamp")] long? Timestamp);

/// <param name="T">The taker's direction as an integer: 1 is a buy, 2 a sell. A number standing for
/// a side, which is why the mapping is written out rather than left to a truthiness test.</param>
/// <param name="V">Volume in CONTRACTS.</param>
/// <summary>
/// One public print. <b>The case of the key is load-bearing:</b> "T" is the side and "t" is the
/// timestamp, two different fields one capital apart — read case-insensitively they are the same
/// field, and <see cref="MexcClient"/> reads this route strictly for exactly that reason.
/// </summary>
internal sealed record MexcDeal(
    [property: JsonPropertyName("i")] string? I,
    [property: JsonPropertyName("p")] double? P,
    [property: JsonPropertyName("v")] double? V,
    [property: JsonPropertyName("T")] int? T,
    [property: JsonPropertyName("t")] long? Time);

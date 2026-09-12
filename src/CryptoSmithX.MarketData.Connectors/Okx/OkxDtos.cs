using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Okx;

/// <summary>
/// OKX's v5 envelope. <c>code</c> is a STRING — "0" on success — and failure arrives inside an HTTP
/// 200, the same shape Bybit and Bitget use and the same reason the status line alone is not an
/// answer.
/// </summary>
internal sealed record OkxEnvelope<T>(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("msg")] string? Msg,
    [property: JsonPropertyName("data")] IReadOnlyList<T>? Data);

/// <summary>
/// One row of <c>/api/v5/market/tickers?instType=SWAP</c>.
/// </summary>
/// <param name="Vol24h">CONTRACTS. <paramref name="VolCcy24h"/> is the same volume in base units,
/// and the pair proves the contract size: on BTC-USDT-SWAP 2 516 624.7 × 0.01 is 25 166.247, which
/// is <c>volCcy24h</c> exactly.</param>
/// <param name="BidSz">Contracts as well, like every size on this venue.</param>
internal sealed record OkxTicker(
    [property: JsonPropertyName("instId")] string InstId,
    [property: JsonPropertyName("last")] string? Last,
    [property: JsonPropertyName("bidPx")] string? BidPx,
    [property: JsonPropertyName("bidSz")] string? BidSz,
    [property: JsonPropertyName("askPx")] string? AskPx,
    [property: JsonPropertyName("askSz")] string? AskSz,
    [property: JsonPropertyName("vol24h")] string? Vol24h,
    [property: JsonPropertyName("volCcy24h")] string? VolCcy24h,
    [property: JsonPropertyName("ts")] string? Ts);

/// <param name="CtVal">Base-asset units per contract — 0.01 BTC on BTC-USDT-SWAP. This venue quotes
/// every size in contracts, so this is what <c>contract_multiplier</c> carries.</param>
/// <param name="InstFamily">"BTC-USDT" — the venue's own split of the pair, and the only place it
/// states one: <c>baseCcy</c> is EMPTY on a swap. It is also the key the index series is published
/// under, which is why the adapter keeps it.</param>
internal sealed record OkxInstrument(
    [property: JsonPropertyName("instId")] string InstId,
    [property: JsonPropertyName("instFamily")] string? InstFamily,
    [property: JsonPropertyName("ctType")] string? CtType,
    [property: JsonPropertyName("ctVal")] string? CtVal,
    [property: JsonPropertyName("ctValCcy")] string? CtValCcy,
    [property: JsonPropertyName("settleCcy")] string? SettleCcy,
    [property: JsonPropertyName("tickSz")] string? TickSz,
    [property: JsonPropertyName("lotSz")] string? LotSz,
    [property: JsonPropertyName("minSz")] string? MinSz,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("listTime")] string? ListTime);

internal sealed record OkxMarkPrice(
    [property: JsonPropertyName("instId")] string InstId,
    [property: JsonPropertyName("markPx")] string? MarkPx);

/// <param name="Oi">Contracts. <paramref name="OiCcy"/> is the same in base units and
/// <paramref name="OiUsd"/> the quote notional the venue computes itself — so oi_quote is written
/// as published and never as our own oi × mark.</param>
internal sealed record OkxOpenInterest(
    [property: JsonPropertyName("instId")] string InstId,
    [property: JsonPropertyName("oi")] string? Oi,
    [property: JsonPropertyName("oiCcy")] string? OiCcy,
    [property: JsonPropertyName("oiUsd")] string? OiUsd,
    [property: JsonPropertyName("ts")] string? Ts);

/// <param name="FundingTime">When the CURRENT period settles. With
/// <paramref name="NextFundingTime"/> beside it the interval is the venue's own arithmetic rather
/// than our assumption — 8 h on BTC-USDT-SWAP, measured as the difference of the two.</param>
internal sealed record OkxFundingRate(
    [property: JsonPropertyName("instId")] string InstId,
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("fundingTime")] string? FundingTime,
    [property: JsonPropertyName("nextFundingTime")] string? NextFundingTime);

/// <summary>The index series, keyed by the PAIR ("BTC-USDT") rather than the instrument
/// ("BTC-USDT-SWAP") — which is what <c>instFamily</c> is for.</summary>
internal sealed record OkxIndexTicker(
    [property: JsonPropertyName("instId")] string InstId,
    [property: JsonPropertyName("idxPx")] string? IdxPx);

internal sealed record OkxFundingHistoryRow(
    [property: JsonPropertyName("instId")] string? InstId,
    [property: JsonPropertyName("fundingRate")] string? FundingRate,
    [property: JsonPropertyName("fundingTime")] string? FundingTime);

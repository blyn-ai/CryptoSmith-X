using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Gmx;

// Shapes of arbitrum.gmxapi.io/v1 as measured 2026-09-13 (its own /swagger.json). Every USD figure and
// every factor is an integer string at PRECISION 1e30; token amounts are integers in the token's own
// decimals; trade prices are per RAW token unit, so a price needs 10^decimals / 1e30 to become dollars
// per coin. The ticker prices are the exception — already per whole token at 1e30.

internal sealed record GmxMarket(
    string Symbol,
    string MarketTokenAddress,
    string IndexTokenAddress,
    string LongTokenAddress,
    string ShortTokenAddress,
    bool IsListed,
    bool IsSpotOnly,
    string? MinPositionSizeUsd);

internal sealed record GmxCapacity(string? AvailableLiquidity);

internal sealed record GmxTicker(
    string Symbol,
    string MarketTokenAddress,
    string? MinPrice,
    string? MaxPrice,
    string? MarkPrice,
    string? LongInterestInTokens,
    string? ShortInterestInTokens,
    string? LongInterestUsd,
    string? ShortInterestUsd,
    string? FundingRateLong,
    string? FundingRateShort,
    GmxCapacity? CapacityLong,
    GmxCapacity? CapacityShort);

/// <summary>The impact function's inputs, from /markets/info. Only the fields the SDK's
/// getPriceImpactForPosition and getCappedPositionImpactUsd read.</summary>
internal sealed record GmxMarketInfo(
    string MarketTokenAddress,
    bool IsDisabled,
    bool UseOpenInterestInTokensForBalance,
    string? LongInterestUsd,
    string? ShortInterestUsd,
    string? LongInterestInTokens,
    string? ShortInterestInTokens,
    string? PositionImpactFactorPositive,
    string? PositionImpactFactorNegative,
    string? PositionImpactExponentFactorPositive,
    string? PositionImpactExponentFactorNegative,
    string? MaxPositionImpactFactorPositive,
    string? MaxPositionImpactFactorNegative,
    string? VirtualIndexTokenId,
    string? VirtualInventoryForPositions,
    string? VirtualInventoryForPositionsInTokens);

internal sealed record GmxPair(
    [property: JsonPropertyName("ticker_id")] string TickerId,
    [property: JsonPropertyName("pool_id")] string PoolId,
    [property: JsonPropertyName("base_volume")] double? BaseVolume,
    [property: JsonPropertyName("target_volume")] double? TargetVolume);

internal sealed record GmxToken(string Symbol, string Address, int Decimals);

internal sealed record GmxTrade(
    string Id,
    string EventName,
    string MarketAddress,
    int OrderType,
    bool IsLong,
    string? ExecutionPrice,
    string? SizeDeltaInTokens,
    string? SizeDeltaUsd,
    long Timestamp);

internal sealed record GmxTradesPage(IReadOnlyList<GmxTrade>? Trades, string? NextCursor, bool HasMore);

internal sealed record GmxOhlcv(long Timestamp, string? Open, string? High, string? Low, string? Close);

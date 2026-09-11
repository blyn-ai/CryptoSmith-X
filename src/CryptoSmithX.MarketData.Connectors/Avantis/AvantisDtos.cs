using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// The venue's catalogue, verbatim: <c>GET data.avantisfi.com/v2/trading</c>. One public call, no
/// key, 120 pairs and 362 KB in 0.81 s measured on 2026-09-11 — the whole venue in one response, so
/// there is no per-symbol sweep here at all.
///
/// <c>pairInfos</c> keys are STRINGS ("1", not 1) and the payload is camelCase; both are the
/// venue's own shape and are not normalised on the way in.
/// </summary>
internal sealed record AvTrading(
    [property: JsonPropertyName("dataVersion")] int DataVersion,
    [property: JsonPropertyName("pairInfos")] Dictionary<string, AvPair>? PairInfos,
    [property: JsonPropertyName("groupInfo")] Dictionary<string, AvGroup>? GroupInfo,
    [property: JsonPropertyName("pairCount")] int PairCount,
    [property: JsonPropertyName("totalOi")] double? TotalOi,
    [property: JsonPropertyName("maxOpenInterest")] double? MaxOpenInterest);

internal sealed record AvGroup(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("groupOI")] double? GroupOi,
    [property: JsonPropertyName("groupMaxOI")] double? GroupMaxOi);

/// <summary>
/// One pair. The live payload carries 119 leaf fields; only what this system can honestly record is
/// declared here, and every omission is deliberate rather than unexplored — the full inventory is
/// in plans/avantis-connection.md §0.
/// </summary>
/// <param name="OpenInterest">In the QUOTE asset (USD notional), by side. Measured: pairOI equals
/// long + short on all 120 pairs to the last digit, which is what makes it the venue's own
/// definition of a pair's open interest rather than our sum.</param>
/// <param name="CoinOi">The same thing in BASE units. Both are published, so nothing is ever
/// derived by dividing one by a price.</param>
internal sealed record AvPair(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("groupIndex")] int GroupIndex,
    [property: JsonPropertyName("isPairListed")] bool IsPairListed,
    [property: JsonPropertyName("openInterest")] AvSides? OpenInterest,
    [property: JsonPropertyName("coinOI")] AvSides? CoinOi,
    [property: JsonPropertyName("pairOI")] double? PairOi,
    [property: JsonPropertyName("pairMaxOI")] double? PairMaxOi,
    [property: JsonPropertyName("blockOILimit")] double? BlockOiLimit,
    [property: JsonPropertyName("liquidity")] AvLiquidity? Liquidity,
    [property: JsonPropertyName("pairParams")] AvPairParams? PairParams,
    [property: JsonPropertyName("priceImpactMultiplier")] double? PriceImpactMultiplier,
    [property: JsonPropertyName("skewImpactMultiplier")] double? SkewImpactMultiplier,
    [property: JsonPropertyName("spreadP")] double? SpreadP,
    [property: JsonPropertyName("decayedVol")] double? DecayedVol,
    [property: JsonPropertyName("minLevPosUSDC")] double? MinLevPosUsdc,
    [property: JsonPropertyName("leverages")] AvLeverages? Leverages,
    [property: JsonPropertyName("feed")] AvFeed? Feed);

internal sealed record AvSides(
    [property: JsonPropertyName("long")] double? Long,
    [property: JsonPropertyName("short")] double? Short);

internal sealed record AvLiquidity(
    [property: JsonPropertyName("buy")] double? Buy,
    [property: JsonPropertyName("sell")] double? Sell);

/// <summary>The gTrade-line depth inputs: the notional that moves the price one percent each way.
/// An INPUT to the impact function, never a measurement of a book — this market has none.</summary>
internal sealed record AvPairParams(
    [property: JsonPropertyName("onePercentDepthAbove")] double? OnePercentDepthAbove,
    [property: JsonPropertyName("onePercentDepthBelow")] double? OnePercentDepthBelow);

internal sealed record AvLeverages(
    [property: JsonPropertyName("minLeverage")] double? MinLeverage,
    [property: JsonPropertyName("maxLeverage")] double? MaxLeverage);

/// <summary>The Pyth feed behind the pair. <see cref="AvFeedAttributes.Symbol"/> is also the key the
/// candle shim is addressed by, which is why the catalogue is what makes candles reachable.</summary>
internal sealed record AvFeed(
    [property: JsonPropertyName("feedId")] string? FeedId,
    [property: JsonPropertyName("attributes")] AvFeedAttributes? Attributes);

/// <param name="IsOpen">Whether the market is trading right now. PYTH'S statement, relayed by
/// Avantis — the provenance the snapshot column's comment names. Null where absent.</param>
internal sealed record AvFeedAttributes(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("assetType")] string? AssetType,
    [property: JsonPropertyName("isOpen")] bool? IsOpen);

/// <summary>The liquidity pool's own state: <c>GET tx-builder.avantisfi.com/v2/lp/state</c>. For a
/// vault-backed venue this pool is the counterparty of every trade, which is the closest thing it
/// has to a book's depth. Venue-wide, not per pair — hence its own table and its own key.</summary>
internal sealed record AvLpEnvelope(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("data")] AvLpState? Data);

/// <param name="TotalAssets">USDC in 1e6, as a string — the API's own convention for on-chain
/// units (GET /v2/meta). Parsed at the edge, never passed on as a string.</param>
/// <param name="UtilizationRatio">1e10, so 488908393315 is 48.89%.</param>
internal sealed record AvLpState(
    [property: JsonPropertyName("totalAssets")] string? TotalAssets,
    [property: JsonPropertyName("totalSupply")] string? TotalSupply,
    [property: JsonPropertyName("utilizationRatio")] string? UtilizationRatio,
    [property: JsonPropertyName("depositCap")] string? DepositCap,
    [property: JsonPropertyName("withdrawThreshold")] string? WithdrawThreshold,
    [property: JsonPropertyName("sharePriceUsdc")] double? SharePriceUsdc);

/// <summary>One bar from the TradingView shim on <c>feed-v3.avantisfi.com</c>. OHLC and NOT OHLCV:
/// there is no volume field, because this is the oracle's price history and not a tape of trades.
/// That is why these land as <c>series = 'index'</c> price candles and never as market candles.</summary>
internal sealed record AvCandle(
    [property: JsonPropertyName("time")] long TimeMs,
    [property: JsonPropertyName("open")] double Open,
    [property: JsonPropertyName("high")] double High,
    [property: JsonPropertyName("low")] double Low,
    [property: JsonPropertyName("close")] double Close);

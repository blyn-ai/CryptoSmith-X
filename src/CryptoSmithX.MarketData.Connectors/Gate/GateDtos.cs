using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Gate;

/// <summary>
/// One row of <c>/api/v4/futures/usdt/tickers</c>. Unlike Bybit and Bitget there is no envelope —
/// v4 returns a bare array and signals failure with the HTTP status, so the status line IS the
/// answer here.
/// </summary>
/// <param name="TotalSize">Open interest in CONTRACTS, not base units. So is
/// <paramref name="Volume24h"/>. Both become base units through the contract's own
/// <c>quanto_multiplier</c>, and the venue's own <c>volume_24h_base</c> is what proves the
/// arithmetic: on BTC_USDT, 207 254 174 contracts × 0.0001 = 20 725, which is that field exactly.
/// Stored raw all the same — the multiplier lives on the instrument and the studio applies it, the
/// way it already does for Kraken's contract size.</param>
internal sealed record GateTicker(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("last")] string? Last,
    [property: JsonPropertyName("highest_bid")] string? HighestBid,
    [property: JsonPropertyName("highest_size")] string? HighestSize,
    [property: JsonPropertyName("lowest_ask")] string? LowestAsk,
    [property: JsonPropertyName("lowest_size")] string? LowestSize,
    [property: JsonPropertyName("mark_price")] string? MarkPrice,
    [property: JsonPropertyName("index_price")] string? IndexPrice,
    [property: JsonPropertyName("funding_rate")] string? FundingRate,
    [property: JsonPropertyName("total_size")] string? TotalSize,
    [property: JsonPropertyName("volume_24h")] string? Volume24h,
    [property: JsonPropertyName("volume_24h_base")] string? Volume24hBase,
    [property: JsonPropertyName("volume_24h_quote")] string? Volume24hQuote);

/// <param name="QuantoMultiplier">Base-asset units per contract — 0.0001 on BTC_USDT. This is the
/// one venue in this batch that quotes sizes in contracts rather than coins, which is exactly what
/// <c>contract_multiplier</c> exists for.</param>
/// <param name="FundingInterval">SECONDS (28800 = 8 h) — a third unit for the same concept across
/// three venues, which is why each adapter converts at its own edge and none of them shares a
/// helper that would hide the difference.</param>
internal sealed record GateContract(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("in_delisting")] bool InDelisting,
    [property: JsonPropertyName("quanto_multiplier")] string? QuantoMultiplier,
    [property: JsonPropertyName("order_price_round")] string? OrderPriceRound,
    [property: JsonPropertyName("order_size_min")] long? OrderSizeMin,
    [property: JsonPropertyName("funding_interval")] int? FundingInterval,
    [property: JsonPropertyName("create_time")] long? CreateTime);

/// <summary>A candle on v4: seconds, not milliseconds, and <c>v</c> is contracts while <c>sum</c>
/// is quote notional.</summary>
internal sealed record GateCandle(
    [property: JsonPropertyName("t")] long T,
    [property: JsonPropertyName("o")] string? O,
    [property: JsonPropertyName("h")] string? H,
    [property: JsonPropertyName("l")] string? L,
    [property: JsonPropertyName("c")] string? C,
    [property: JsonPropertyName("v")] double? V,
    [property: JsonPropertyName("sum")] string? Sum);

internal sealed record GateFundingRow(
    [property: JsonPropertyName("t")] long T,
    [property: JsonPropertyName("r")] string? R);

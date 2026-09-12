using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.CoinbaseIntx;

/// <summary>
/// One row of <c>/api/v1/instruments</c> — the single call that carries this venue's whole market.
/// No envelope: v1 returns a bare array and puts failure in the HTTP status.
/// </summary>
/// <param name="FundingInterval">NANOSECONDS, as a string: "3600000000000" is one hour. A fourth
/// unit for the same concept across six venues — hours on Bybit's ticker, minutes on its own
/// instruments route, hours-as-a-string on Bitget, seconds on Gate, the gap between two instants on
/// OKX, and nanoseconds here.</param>
/// <param name="OpenInterest">BASE units, not contracts: 1 671.64 against a 77k price is coins. This
/// venue quotes everything in base, so nothing scales.</param>
internal sealed record IntxInstrument(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("base_asset_name")] string? BaseAssetName,
    [property: JsonPropertyName("quote_asset_name")] string? QuoteAssetName,
    [property: JsonPropertyName("base_increment")] string? BaseIncrement,
    [property: JsonPropertyName("quote_increment")] string? QuoteIncrement,
    [property: JsonPropertyName("min_quantity")] string? MinQuantity,
    [property: JsonPropertyName("min_notional_value")] string? MinNotionalValue,
    [property: JsonPropertyName("funding_interval")] string? FundingInterval,
    [property: JsonPropertyName("trading_state")] string? TradingState,
    [property: JsonPropertyName("open_interest")] string? OpenInterest,
    [property: JsonPropertyName("qty_24hr")] string? Qty24hr,
    [property: JsonPropertyName("notional_24hr")] string? Notional24hr,
    [property: JsonPropertyName("quote")] IntxQuote? Quote);

/// <param name="PredictedFunding">A FORECAST of the next rate, not the rate in force. It goes to
/// <c>funding_rate_predicted</c> and never to <c>funding_rate</c> — the realised series lives on
/// <c>/instruments/{symbol}/funding</c>, which is a call per instrument. The roadmap flagged this as
/// the least certain thing it claimed about this venue, and it was right to.</param>
/// <param name="Timestamp">ISO-8601, not epoch milliseconds — the venue's own clock for the frame.</param>
internal sealed record IntxQuote(
    [property: JsonPropertyName("best_bid_price")] string? BestBidPrice,
    [property: JsonPropertyName("best_bid_size")] string? BestBidSize,
    [property: JsonPropertyName("best_ask_price")] string? BestAskPrice,
    [property: JsonPropertyName("best_ask_size")] string? BestAskSize,
    [property: JsonPropertyName("trade_price")] string? TradePrice,
    [property: JsonPropertyName("index_price")] string? IndexPrice,
    [property: JsonPropertyName("mark_price")] string? MarkPrice,
    [property: JsonPropertyName("predicted_funding")] string? PredictedFunding,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp);

internal sealed record IntxCandles(
    [property: JsonPropertyName("aggregations")] IReadOnlyList<IntxCandle>? Aggregations);

internal sealed record IntxCandle(
    [property: JsonPropertyName("start")] DateTimeOffset? Start,
    [property: JsonPropertyName("open")] string? Open,
    [property: JsonPropertyName("high")] string? High,
    [property: JsonPropertyName("low")] string? Low,
    [property: JsonPropertyName("close")] string? Close,
    [property: JsonPropertyName("volume")] string? Volume);

internal sealed record IntxFundingPage(
    [property: JsonPropertyName("results")] IReadOnlyList<IntxFundingRow>? Results);

internal sealed record IntxFundingRow(
    [property: JsonPropertyName("funding_rate")] string? FundingRate,
    [property: JsonPropertyName("event_time")] DateTimeOffset? EventTime);

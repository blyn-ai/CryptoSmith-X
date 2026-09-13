using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Nado;

/// <summary>
/// One perpetual in the archive's <c>/v2/contracts</c>, which answers for the whole venue keyed by ticker.
///
/// <b>Units, measured 2026-09-13 on BTC-PERP_USDT0:</b> <c>base_volume</c> and <c>open_interest</c> are the
/// BASE asset (1 021.6 and 255.0 BTC); <c>quote_volume</c> is USDT0 (78.7 M, which is the base at the
/// day's price). <c>funding_rate</c> is expressed in 24-HOUR terms (0.000201), the same figure the
/// archive's funding_rate query calls "latest 24hr funding rate" — while the realised series settles
/// HOURLY (8.37e-6 an hour, and 8.37e-6 × 24 = 0.000201). <c>next_funding_rate_timestamp</c> is seconds.
/// </summary>
internal sealed record NadoContract(
    [property: JsonPropertyName("product_id")] int ProductId,
    [property: JsonPropertyName("ticker_id")] string TickerId,
    [property: JsonPropertyName("base_currency")] string? BaseCurrency,
    [property: JsonPropertyName("quote_currency")] string? QuoteCurrency,
    [property: JsonPropertyName("last_price")] double? LastPrice,
    [property: JsonPropertyName("base_volume")] double? BaseVolume,
    [property: JsonPropertyName("quote_volume")] double? QuoteVolume,
    [property: JsonPropertyName("product_type")] string? ProductType,
    [property: JsonPropertyName("open_interest")] double? OpenInterest,
    [property: JsonPropertyName("open_interest_usd")] double? OpenInterestUsd,
    [property: JsonPropertyName("index_price")] double? IndexPrice,
    [property: JsonPropertyName("mark_price")] double? MarkPrice,
    [property: JsonPropertyName("funding_rate")] double? FundingRate24h,
    [property: JsonPropertyName("next_funding_rate_timestamp")] long? NextFundingRateTimestamp);

internal sealed record NadoSymbolsEnvelope(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("data")] NadoSymbolsData? Data);

internal sealed record NadoSymbolsData(
    [property: JsonPropertyName("symbols")] IReadOnlyDictionary<string, NadoSymbol>? Symbols);

/// <summary>Gateway symbol specification. Every number is an x18 integer string — the value times
/// 10^18 — and <c>min_size</c> is a QUOTE notional (100 USDT0 on BTC-PERP), not a quantity.</summary>
internal sealed record NadoSymbol(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("product_id")] int ProductId,
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("price_increment_x18")] string? PriceIncrementX18,
    [property: JsonPropertyName("size_increment")] string? SizeIncrementX18,
    [property: JsonPropertyName("min_size")] string? MinSizeX18);

/// <summary>Gateway <c>/v2/orderbook</c>: floats, best first, and the venue's own clock in milliseconds.
/// At most 100 levels a side whatever depth is asked for — measured at 100, 200, 500 and 1 000.</summary>
internal sealed record NadoBook(
    [property: JsonPropertyName("bids")] IReadOnlyList<double[]>? Bids,
    [property: JsonPropertyName("asks")] IReadOnlyList<double[]>? Asks,
    [property: JsonPropertyName("timestamp")] long? Timestamp);

/// <summary>
/// One row of the archive's <c>/v2/trades</c>. <b>Every match appears TWICE</b> — once per side, sharing
/// a <c>trade_id</c>, each leg priced with its own fee folded in (76 613.0 and 76 624.49 for one fill of
/// 0.0074 BTC). Only the instant is read from this route.
/// </summary>
internal sealed record NadoTrade(
    [property: JsonPropertyName("trade_id")] long? TradeId,
    [property: JsonPropertyName("price")] double? Price,
    [property: JsonPropertyName("base_filled")] double? BaseFilled,
    [property: JsonPropertyName("timestamp")] long? Timestamp,
    [property: JsonPropertyName("trade_type")] string? TradeType);

internal sealed record NadoCandles(
    [property: JsonPropertyName("candlesticks")] IReadOnlyList<NadoCandle>? Candlesticks);

internal sealed record NadoCandle(
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("open_x18")] string? OpenX18,
    [property: JsonPropertyName("high_x18")] string? HighX18,
    [property: JsonPropertyName("low_x18")] string? LowX18,
    [property: JsonPropertyName("close_x18")] string? CloseX18,
    [property: JsonPropertyName("volume")] string? VolumeX18);

internal sealed record NadoFundingHistory(
    [property: JsonPropertyName("funding_rates")] IReadOnlyList<NadoFundingRow>? FundingRates);

/// <param name="FundingRateX18">A realised HOURLY rate, times 10^18.</param>
internal sealed record NadoFundingRow(
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("funding_rate_x18")] string? FundingRateX18);

internal sealed record NadoEvents(
    [property: JsonPropertyName("events")] IReadOnlyList<NadoEvent>? Events,
    [property: JsonPropertyName("txs")] IReadOnlyList<NadoTx>? Txs);

internal sealed record NadoEvent(
    [property: JsonPropertyName("subaccount")] string? Subaccount,
    [property: JsonPropertyName("product_id")] int ProductId,
    [property: JsonPropertyName("submission_idx")] string? SubmissionIdx,
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("pre_balance")] JsonElement PreBalance,
    [property: JsonPropertyName("post_balance")] JsonElement PostBalance);

internal sealed record NadoTx(
    [property: JsonPropertyName("submission_idx")] string? SubmissionIdx,
    [property: JsonPropertyName("timestamp")] string? Timestamp);

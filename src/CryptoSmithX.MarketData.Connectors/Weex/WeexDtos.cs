using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Weex;

// The JSON shapes of the WEEX Futures public REST responses the adapter reads (base host
// https://api-contract.weex.com), captured from the live API. Only the fields the adapter maps are
// declared. camelCase fields bind case-insensitively (Web defaults); snake_case ones carry an
// explicit name. WEEX runs two API generations on the same host — /capi/v2 (native, symbols like
// 'cmt_btcusdt') and /capi/v3 (a Binance-format clone, symbols like 'BTCUSDT') — the adapter reads
// identity and most fields from v2 and only reaches into v3 for the one thing v2 lacks: book size.

/// <summary>One row of <c>/capi/v2/market/contracts</c>. tick_size and size_increment are DECIMAL
/// PLACE COUNTS, not raw step values (verified against live prices: BTC last=78235.5, tick_size=1;
/// DOGE last=0.08284, tick_size=5) — the adapter converts with 10^-n.</summary>
internal sealed record WeexContract
{
    public string Symbol { get; init; } = "";
    [JsonPropertyName("underlying_index")] public string UnderlyingIndex { get; init; } = "";
    [JsonPropertyName("quote_currency")] public string QuoteCurrency { get; init; } = "";
    [JsonPropertyName("tick_size")] public int TickSize { get; init; }
    [JsonPropertyName("size_increment")] public int SizeIncrement { get; init; }
    public string MinOrderSize { get; init; } = "0";
}

/// <summary>One row of <c>/capi/v2/market/tickers</c>. No bid/ask size, no funding rate, no open
/// interest — each comes from a separate call the adapter merges in.</summary>
internal sealed record WeexTicker
{
    public string Symbol { get; init; } = "";
    public string Last { get; init; } = "0";
    [JsonPropertyName("best_bid")] public string BestBid { get; init; } = "0";
    [JsonPropertyName("best_ask")] public string BestAsk { get; init; } = "0";
    [JsonPropertyName("volume_24h")] public string Volume24h { get; init; } = "0";
    public string MarkPrice { get; init; } = "0";
    public string IndexPrice { get; init; } = "0";
}

/// <summary>One row of <c>/capi/v3/market/ticker/bookTicker</c> — the only batched source of top-of-
/// book size. Symbol is Binance-format ('BTCUSDT'); the adapter transforms v2's 'cmt_btcusdt' to
/// match (strip 'cmt_', uppercase) rather than trusting the two APIs share an identity.</summary>
internal sealed record WeexBookTicker
{
    public string Symbol { get; init; } = "";
    public string BidQty { get; init; } = "0";
    public string AskQty { get; init; } = "0";
}

/// <summary>One row of <c>/capi/v2/market/currentFundRate</c> (batched, all symbols in one call).
/// collectCycle is the funding interval in MINUTES and varies per symbol (60/240/480 observed) —
/// unlike Kraken there is no single fixed interval.</summary>
internal sealed record WeexFundingRate
{
    public string Symbol { get; init; } = "";
    public string FundingRate { get; init; } = "0";
    public int CollectCycle { get; init; }
}

/// <summary>Response of <c>/capi/v2/market/open_interest?symbol=..</c> — per-symbol only, no batch
/// variant exists on either API generation. base_volume is OI in base-asset units.</summary>
internal sealed record WeexOpenInterest
{
    [JsonPropertyName("base_volume")] public string BaseVolume { get; init; } = "0";
}

/// <summary>One row of <c>/capi/v2/market/getHistoryFundRate</c> — newest first, capped at 100
/// entries with no pagination lever, so a symbol funding hourly cannot fully backfill a 7-day window
/// in one call (the collector's own incremental catch-up covers it over later passes).</summary>
internal sealed record WeexFundingHistoryRow
{
    public string FundingRate { get; init; } = "0";
    public long FundingTime { get; init; }
}

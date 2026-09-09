using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

// Shapes of the Kraken Futures public WS frames the feed maps (wss://futures.kraken.com/ws/v1),
// captured from the live socket. Only the fields the adapter uses are declared. camelCase fields
// bind case-insensitively (Web defaults); snake_case ones carry an explicit name. Book deltas are
// not a record — they arrive as a firehose and the feed reads their fields straight off the document.

internal sealed record KrakenWsTicker
{
    [JsonPropertyName("product_id")] public string ProductId { get; init; } = "";
    public double Bid { get; init; }
    public double Ask { get; init; }
    [JsonPropertyName("bid_size")] public double BidSize { get; init; }
    [JsonPropertyName("ask_size")] public double AskSize { get; init; }
    public double Last { get; init; }
    public double MarkPrice { get; init; }
    public double Index { get; init; }
    public double OpenInterest { get; init; }
    public double VolumeQuote { get; init; }

    /// <summary>Fraction of notional per interval — Kraken relativises it on the WS ticker already.</summary>
    [JsonPropertyName("relative_funding_rate")] public double RelativeFundingRate { get; init; }

    /// <summary>Same relativisation as <see cref="RelativeFundingRate"/>, for the FORECAST period
    /// (0030 funding_rate_predicted) rather than the current one.</summary>
    [JsonPropertyName("relative_funding_rate_prediction")] public double RelativeFundingRatePrediction { get; init; }

    /// <summary>When the next funding payment settles (0030 next_funding_at), unix milliseconds. Live
    /// on this WS ticker frame — confirmed in Fixtures/kraken-ws/ticker.json — but absent from the
    /// REST /tickers response's per-symbol row (checked against Fixtures/kraken/tickers.json, which
    /// carries fundingRatePrediction but nothing named next_funding_rate_time or similar).</summary>
    [JsonPropertyName("next_funding_rate_time")] public long NextFundingRateTime { get; init; }

    /// <summary>24h turnover in the base asset, independent of <see cref="VolumeQuote"/> (0030
    /// volume_24h_base).</summary>
    public double Volume { get; init; }

    /// <summary>Event time, unix milliseconds.</summary>
    public long Time { get; init; }
}

internal sealed record KrakenWsBookSnapshot
{
    [JsonPropertyName("product_id")] public string ProductId { get; init; } = "";
    public long Timestamp { get; init; }
    public long Seq { get; init; }
    public List<KrakenWsLevel> Bids { get; init; } = [];
    public List<KrakenWsLevel> Asks { get; init; } = [];
}

internal sealed record KrakenWsLevel
{
    public double Price { get; init; }
    public double Qty { get; init; }
}

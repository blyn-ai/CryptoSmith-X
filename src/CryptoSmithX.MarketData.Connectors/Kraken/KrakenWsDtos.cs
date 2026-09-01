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

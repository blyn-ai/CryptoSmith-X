using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Binance;

// The WebSocket frames of the Binance USDⓈ-M public stream that this feed reads, captured verbatim
// into Fixtures/binance-ws. Bound with BinanceJson.Options — see the long note on that field, which
// exists because of exactly these two records.

/// <summary>
/// The combined-stream envelope. Connecting to <c>/public/stream</c> (rather than <c>/public/ws</c>)
/// wraps every payload as <c>{"stream":"btcusdt@depth@100ms","data":{...}}</c>, which is what makes
/// one connection able to carry every symbol: the frame says which stream it came from instead of
/// the connection implying it.
/// </summary>
internal sealed record BinanceStreamEnvelope
{
    public string Stream { get; init; } = "";
}

/// <summary>
/// One <c>depthUpdate</c> payload, and the reason <see cref="BinanceJson"/> cannot use
/// <c>JsonSerializerDefaults.Web</c>. This single record carries TWO pairs of fields whose names
/// differ only in case — <c>e</c> (event type) beside <c>E</c> (event time), and <c>u</c> (last
/// update id) beside <c>U</c> (first update id) — and case-insensitive binding cannot resolve either
/// pair: System.Text.Json throws <c>InvalidOperationException</c> when it builds the converter, not
/// when it meets a surprising value. <c>BinanceWsProtocolTests</c> pins both halves of that: the
/// record binds correctly under the options this connector uses, and throws under the Web defaults
/// every other connector here uses.
/// </summary>
internal sealed record BinanceWsDepth
{
    [JsonPropertyName("e")] public string EventType { get; init; } = "";

    [JsonPropertyName("E")] public long EventTime { get; init; }

    [JsonPropertyName("s")] public string Symbol { get; init; } = "";

    /// <summary>First update id covered by this frame. Used only at the seam with a REST snapshot.</summary>
    [JsonPropertyName("U")] public long FirstUpdateId { get; init; }

    /// <summary>Last update id covered by this frame — what the NEXT frame's <c>pu</c> must equal.</summary>
    [JsonPropertyName("u")] public long LastUpdateId { get; init; }

    /// <summary>The previous frame's <c>u</c>. USDⓈ-M carries this and spot does not, which is why
    /// the two Binance markets have different steady-state rules and why a spot implementation
    /// cannot be reused here.</summary>
    [JsonPropertyName("pu")] public long PreviousUpdateId { get; init; }

    /// <summary>Bid levels changed by this frame, as <c>[price, qty]</c> string pairs. A qty of "0"
    /// is a removal.</summary>
    [JsonPropertyName("b")] public List<string[]> Bids { get; init; } = [];

    [JsonPropertyName("a")] public List<string[]> Asks { get; init; } = [];
}

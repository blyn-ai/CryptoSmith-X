using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>One entry of <c>meta</c>'s universe — a perp's static definition. Field names already
/// match Web camelCase, so no explicit JsonPropertyName is needed here (unlike the candle DTO, whose
/// single-letter keys collide case-insensitively).</summary>
internal sealed record HlUniverseEntry
{
    public int SzDecimals { get; init; }
    public string Name { get; init; } = "";
    public bool IsDelisted { get; init; }
}

internal sealed record HlMeta
{
    public List<HlUniverseEntry> Universe { get; init; } = [];
}

/// <summary>One entry of <c>metaAndAssetCtxs</c>'s second array element — index-aligned with
/// <c>meta.universe</c>, not keyed by symbol. No last-trade-price field exists at this batch scale;
/// <see cref="MidPx"/> stands in for it (see the adapter's doc comment).</summary>
internal sealed record HlAssetCtx
{
    public string Funding { get; init; } = "0";
    public string OpenInterest { get; init; } = "0";
    public string DayNtlVlm { get; init; } = "0";
    public string OraclePx { get; init; } = "0";
    public string MarkPx { get; init; } = "0";
    public string? MidPx { get; init; }
}

/// <summary>One price level of an <c>l2Book</c> response. Public: shared by both live feeds and their
/// tests, same as <see cref="Kraken.DepthMath"/>'s inputs.</summary>
public sealed record HlLevel
{
    public string Px { get; init; } = "0";
    public string Sz { get; init; } = "0";
}

/// <summary>
/// <c>levels[0]</c> is bids (best first), <c>levels[1]</c> is asks (best first) — confirmed live.
/// A symbol with no market serves both as empty arrays, not null.
/// </summary>
public sealed record HlL2Book
{
    public List<List<HlLevel>> Levels { get; init; } = [];
}

/// <summary>One 1-minute bar from <c>candleSnapshot</c>. Single-letter keys: explicit
/// <see cref="JsonPropertyNameAttribute"/> throughout, since case-insensitive matching would
/// otherwise collide open-time "t" with close-time "T" if both were mapped.</summary>
internal sealed record HlCandle
{
    [JsonPropertyName("t")] public long OpenTimeMs { get; init; }
    [JsonPropertyName("o")] public string Open { get; init; } = "0";
    [JsonPropertyName("h")] public string High { get; init; } = "0";
    [JsonPropertyName("l")] public string Low { get; init; } = "0";
    [JsonPropertyName("c")] public string Close { get; init; } = "0";
    [JsonPropertyName("v")] public string Volume { get; init; } = "0";
    // Nullable, because a bar the venue sent without "n" is a bar whose trade count we do not know,
    // and a non-nullable int turned that into a measured zero — the one series in this system that
    // carries trade counts at all was quietly inventing "nobody traded".
    [JsonPropertyName("n")] public int? TradeCount { get; init; }
}

/// <summary>One row of <c>fundingHistory</c>. Unlike WEEX, the venue honours start/end time on this
/// call, so the adapter passes the window straight through instead of windowing locally.</summary>
internal sealed record HlFundingHistoryRow
{
    public string FundingRate { get; init; } = "0";
    public long Time { get; init; }
}

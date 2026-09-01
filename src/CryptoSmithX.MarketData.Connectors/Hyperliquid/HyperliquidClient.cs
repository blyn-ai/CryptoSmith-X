using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>
/// Thin HTTP over Hyperliquid's public <c>/info</c> endpoint. Unlike Kraken and WEEX, the whole API is
/// one POST route distinguished by a <c>"type"</c> field in the body, not separate GET paths. No keys,
/// no retry/logging/back-off — a non-success status or empty body throws and the collector loop above
/// counts it.
/// </summary>
public sealed class HyperliquidClient
{
    private static readonly HttpClient Shared = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Web's default case-insensitive matching would collide candleSnapshot's single-letter keys: "t"
    // (open time) and "T" (close time) both match a property named "t" case-insensitively, and the
    // one that appears later in the JSON silently wins. HlCandle's property names are exact-case
    // single letters, so case-sensitive matching is required here specifically.
    private static readonly JsonSerializerOptions CandleJson = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    private readonly HttpClient _http;
    private readonly string _infoUrl;

    public HyperliquidClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public HyperliquidClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _infoUrl = $"{baseUrl.TrimEnd('/')}/info";
    }

    internal Task<HlMeta> GetMetaAsync(CancellationToken ct) =>
        PostAsync<HlMeta>(new { type = "meta" }, ct);

    /// <summary>Universe (same shape as <c>meta</c>) plus one context per entry, index-aligned —
    /// the only batched source of mark/oracle/funding/OI/volume, all in one call.</summary>
    internal async Task<(HlMeta Meta, List<HlAssetCtx> Ctxs)> GetMetaAndAssetCtxsAsync(CancellationToken ct)
    {
        var pair = await PostAsync<JsonElement[]>(new { type = "metaAndAssetCtxs" }, ct);
        if (pair.Length != 2)
        {
            throw new InvalidOperationException($"Hyperliquid metaAndAssetCtxs returned {pair.Length} elements, expected 2");
        }

        var meta = pair[0].Deserialize<HlMeta>(Json) ?? throw new InvalidOperationException("Hyperliquid metaAndAssetCtxs: empty meta element");
        var ctxs = pair[1].Deserialize<List<HlAssetCtx>>(Json) ?? throw new InvalidOperationException("Hyperliquid metaAndAssetCtxs: empty ctxs element");
        return (meta, ctxs);
    }

    /// <summary>Top-of-book plus enough depth for the 50 bps band, for one coin. A coin with no market
    /// serves both levels as empty arrays.</summary>
    internal Task<HlL2Book> GetL2BookAsync(string coin, CancellationToken ct) =>
        PostAsync<HlL2Book>(new { type = "l2Book", coin }, ct);

    /// <summary>1-minute bars in <c>[from, to]</c>. The venue has no documented row cap on this call
    /// (unlike WEEX's fixed 100), but the window is still bounded by the caller to what is needed.</summary>
    internal Task<List<HlCandle>> GetCandles1mAsync(string coin, long fromMs, long toMs, CancellationToken ct) =>
        PostAsync<List<HlCandle>>(new
        {
            type = "candleSnapshot",
            req = new { coin, interval = "1m", startTime = fromMs, endTime = toMs },
        }, ct, CandleJson);

    /// <summary>Funding history in <c>[from, to]</c> — the venue honours the window server-side.</summary>
    internal Task<List<HlFundingHistoryRow>> GetFundingHistoryAsync(string coin, long fromMs, long toMs, CancellationToken ct) =>
        PostAsync<List<HlFundingHistoryRow>>(new { type = "fundingHistory", coin, startTime = fromMs, endTime = toMs }, ct);

    private async Task<T> PostAsync<T>(object body, CancellationToken ct, JsonSerializerOptions? readOptions = null)
    {
        using var response = await _http.PostAsJsonAsync(_infoUrl, body, Json, ct);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(readOptions ?? Json, ct);
        return value ?? throw new InvalidOperationException($"Hyperliquid returned an empty body for {body}");
    }
}

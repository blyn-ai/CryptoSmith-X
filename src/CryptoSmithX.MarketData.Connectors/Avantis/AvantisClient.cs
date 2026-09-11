using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// Thin HTTP over Avantis's public surface. No keys and no orders, so every call here is
/// unauthenticated — measured, not assumed: the OpenAPI at tx-builder.avantisfi.com declares
/// <c>security: []</c> on all 62 GET routes, and the data service and candle shim answer without a
/// header at all. Deliberately dumb, like its four siblings: no retry, no logging, no back-off — a
/// non-success status throws and the collector loop counts it.
///
/// <b>THREE HOSTS, and that is the venue's shape rather than ours.</b> The catalogue, the vault and
/// the price history live apart:
///
///   data.avantisfi.com        the whole catalogue in one call — fees, OI, impact inputs, hours
///   tx-builder.avantisfi.com  the liquidity pool's own state, venue-wide
///   feed-v3.avantisfi.com     the oracle's OHLC, addressed by the Pyth symbol
///
/// Only the first is <c>segment.base_url</c>. The other two are constants here because a segment
/// row holds ONE base address, and inventing columns for a second and third to serve one venue is a
/// schema change made for a connector's convenience.
/// </summary>
public sealed class AvantisClient
{
    /// <summary>The ERC-4626 tranche behind every trade. Not configurable per segment for the
    /// reason in the class remarks.</summary>
    private const string VaultHost = "https://tx-builder.avantisfi.com";

    /// <summary>The oracle's price history, through a TradingView-shaped shim. Measured: resolutions
    /// 1/5/60/240/D all answer, and 1440 one-minute bars come back for a full day with no gaps.</summary>
    private const string FeedHost = "https://feed-v3.avantisfi.com";

    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AvantisClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler, so the HTTP + JSON +
    /// mapping path is exercised end to end without a network.</summary>
    public AvantisClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>The entire venue in one response: every pair's fees, leverage, OI in both units,
    /// impact inputs, and whether it is trading right now.</summary>
    internal Task<AvTrading> GetTradingAsync(CancellationToken ct) =>
        GetAsync<AvTrading>($"{_baseUrl}/v2/trading", ct);

    /// <summary>The same response as text. Discovery versions each pair's own payload as its
    /// specification, and the socket feed merges diffs into it — both want the JSON, not a shape
    /// this build happened to declare, so fetching it twice would be a second call for a response
    /// already in hand.</summary>
    internal async Task<string> GetTradingRawAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/v2/trading", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>The liquidity pool's state — one row for the whole venue.</summary>
    internal async Task<AvLpState?> GetVaultStateAsync(CancellationToken ct) =>
        (await GetAsync<AvLpEnvelope>($"{VaultHost}/v2/lp/state", ct)).Data;

    /// <summary>Closed bars of the ORACLE's price, by Pyth symbol (<c>Crypto.BTC/USD</c>) — the
    /// spelling the catalogue already carries on every pair.</summary>
    internal Task<IReadOnlyList<AvCandle>> GetCandlesAsync(
        string pythSymbol, string resolution, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<IReadOnlyList<AvCandle>>(
            $"{FeedHost}/v1/shims/tradingview/history"
            + $"?symbol={Uri.EscapeDataString(pythSymbol)}"
            + $"&resolution={Uri.EscapeDataString(resolution)}"
            + $"&from={from.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}"
            + $"&to={to.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException($"Avantis returned an empty body for {url}");
    }

    /// <summary>
    /// A quantity the API sends as a decimal string in on-chain units, into a human number.
    ///
    /// The scales are the venue's own, from <c>GET /v2/meta</c>: USDC is 1e6 and the utilisation
    /// ratio is 1e10. Parsed here rather than carried onward as a string, and invariant-culture
    /// because a decimal point is not a local preference.
    /// </summary>
    internal static double? Scaled(string? raw, double scale)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value / scale;
    }

    internal const double UsdcScale = 1e6;

    /// <summary>
    /// The utilisation ratio, into a FRACTION.
    ///
    /// The venue documents the field at 1e10, and that is true — but what 1e10 yields is a
    /// PERCENTAGE: the live value 488908393315 becomes 48.8908, not 0.488908. The column stores a
    /// fraction (0.4889 = 48.89%, as its own comment says), so the scale here is the venue's own
    /// times a hundred. Named rather than folded into the constant, because "1e12" on its own would
    /// look like a transcription error against the venue's documentation.
    /// </summary>
    internal const double RatioScale = 1e10 * 100;
}

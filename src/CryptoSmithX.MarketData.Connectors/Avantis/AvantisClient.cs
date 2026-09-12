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

    /// <summary>The risk engine and the core backend, both behind one routing host. The engine is
    /// what answers "at what price would you fill me, this size, this side" — the venue's own
    /// replacement for a book, and the reason the quote columns are not empty. Measured anonymously
    /// on mainnet: HTTP 201 in 0.19 s, 53.6 req/s at concurrency 16 with no throttling.</summary>
    private const string ApiHost = "https://prod-api.avantisfi.com";

    /// <summary>Settled history, including the venue's own public trade tape. A separate host again,
    /// and again not ours to fold into a segment column.</summary>
    private const string HistoryHost = "https://api.avantisfi.com";

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

    /// <summary>
    /// One quoted spread, for one size and one side.
    ///
    /// <b>Anonymous on purpose.</b> The zero address is the venue's own UI fallback for an
    /// unconnected visitor (the SDK says so in as many words), so this is a read of a public price,
    /// not an order and not an impersonation.
    ///
    /// <b>A refusal is an answer, not a failure.</b> 403 is a shut or blocked market and 503 is
    /// SM004 — "the mechanism matched but no spread is computable". Both mean "no quote at this
    /// size", which is exactly what the search above is looking for when it walks outward; throwing
    /// would turn the end of the curve into a collector error. Measured: across 32 calls the split
    /// was identical at concurrency 1, 8 and 16 (17 × 201, 11 × 403, 4 × 503), which is what proves
    /// these are properties of the PAIR rather than throttling.
    ///
    /// Returns the quoted percentage — the with-flow estimate when the engine offers one, else the
    /// without-flow figure, the same precedence the SDK applies.
    /// </summary>
    internal async Task<double?> GetSpreadPctAsync(
        int pairIndex, double coinSize, bool isLong, CancellationToken ct)
    {
        var body = new AvSpreadRequest(
            pairIndex,
            "0x0000000000000000000000000000000000000000",
            Raw10(coinSize),
            isLong,
            IsOpen: true,
            OrderType: 0);

        using var response = await _http.PostAsJsonAsync($"{ApiHost}/risk/v2/spread", body, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var quote = await response.Content.ReadFromJsonAsync<AvSpreadResponse>(Json, ct);
        var withFlow = Scaled(quote?.EstimatedSpreadPctWithFlow10, Pct10Scale);
        return withFlow ?? Scaled(quote?.SpreadPctWithoutFlow10, Pct10Scale);
    }

    /// <summary>
    /// The venue's own public trade tape for one pair — market-wide, not per trader.
    ///
    /// This is the surface an earlier audit missed, and missing it is what produced the conclusion
    /// that Last, Turnover and Liquidations could never be filled. Measured: ten records per call,
    /// and those ten span four hours on ETH, so a once-a-minute poll cannot lose a trade.
    /// </summary>
    internal async Task<IReadOnlyList<AvTrade>> GetRecentTradesAsync(int pairIndex, CancellationToken ct) =>
        (await GetAsync<AvTradeEnvelope>(
            $"{HistoryHost}/v1/history/recent-trades/{pairIndex.ToString(CultureInfo.InvariantCulture)}",
            ct)).History ?? [];

    /// <summary>Open interest with the PENDING legs, which the catalogue snapshot does not
    /// carry — orders the operator has accepted but not yet filled.</summary>
    internal async Task<IReadOnlyList<AvOpenInterest>> GetOpenInterestsAsync(CancellationToken ct) =>
        (await GetAsync<AvOpenInterestEnvelope>($"{ApiHost}/core/v2/open-interests", ct)).OpenInterests ?? [];

    /// <summary>A size, into the engine's own fixed-point. Invariant culture and no exponent: the
    /// field is a decimal STRING on the wire, and "1E+11" is not one.</summary>
    private static string Raw10(double size) =>
        ((long)Math.Round(size * 1e10, MidpointRounding.AwayFromZero))
        .ToString(CultureInfo.InvariantCulture);

    /// <summary>The engine publishes percentages at 1e10, the same fixed-point it uses for prices
    /// and leverage.</summary>
    private const double Pct10Scale = 1e10;

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

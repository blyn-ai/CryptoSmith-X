using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Okx;

/// <summary>
/// Thin HTTP over OKX's public v5 REST. Unauthenticated, no retry, no back-off.
///
/// <b>A 200 is not a success.</b> v5 puts a string <c>code</c> in the body and "0" is the only one
/// that means anything went right.
///
/// <b>The limiter is keyed by ENDPOINT × instId, which is why the bulk calls are what this adapter
/// uses.</b> Reproduced: 30 requests to <c>funding-rate?instId=BTC-USDT-SWAP</c> gave
/// <c>{200: 10, 429: 20}</c>, while 30 requests across 30 DIFFERENT instIds gave <c>{200: 30}</c> —
/// both routes documented as "20 requests per 2 seconds", both delivering half that per key.
/// <c>VenueGate</c> is a single scalar and expresses none of that shape; it is safe here and simply
/// under-uses the venue, which is the trade this adapter accepts by never sweeping per symbol.
///
/// <b>One segment's whole market is five calls.</b> Measured: tickers 479 rows, mark-price 479,
/// open-interest 479 with its own <c>ts</c>, funding-rate 658, index-tickers 1075 — each under
/// 0.4 s.
/// </summary>
public sealed class OkxClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string InstType = "SWAP";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OkxClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public OkxClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    internal Task<IReadOnlyList<OkxInstrument>> GetInstrumentsAsync(CancellationToken ct) =>
        GetAsync<OkxInstrument>($"{_baseUrl}/api/v5/public/instruments?instType={InstType}", ct);

    internal Task<IReadOnlyList<OkxTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<OkxTicker>($"{_baseUrl}/api/v5/market/tickers?instType={InstType}", ct);

    internal Task<IReadOnlyList<OkxMarkPrice>> GetMarkPricesAsync(CancellationToken ct) =>
        GetAsync<OkxMarkPrice>($"{_baseUrl}/api/v5/public/mark-price?instType={InstType}", ct);

    internal Task<IReadOnlyList<OkxOpenInterest>> GetOpenInterestAsync(CancellationToken ct) =>
        GetAsync<OkxOpenInterest>($"{_baseUrl}/api/v5/public/open-interest?instType={InstType}", ct);

    /// <summary><c>instId=ANY</c> is the venue's own wildcard for the whole segment — 658 rows in one
    /// call, against the per-instId route whose limiter is the one measured above.</summary>
    internal Task<IReadOnlyList<OkxFundingRate>> GetFundingRatesAsync(CancellationToken ct) =>
        GetAsync<OkxFundingRate>($"{_baseUrl}/api/v5/public/funding-rate?instId=ANY", ct);

    /// <summary>The index series for one quote currency. Keyed by the PAIR, joined back through
    /// <c>instFamily</c>; a swap settled in something other than <paramref name="quoteCcy"/> simply
    /// has no row here, and its index stays absent rather than borrowed from another currency.</summary>
    internal Task<IReadOnlyList<OkxIndexTicker>> GetIndexTickersAsync(string quoteCcy, CancellationToken ct) =>
        GetAsync<OkxIndexTicker>($"{_baseUrl}/api/v5/market/index-tickers?quoteCcy={Uri.EscapeDataString(quoteCcy)}", ct);

    /// <summary>Closed 1-minute bars as string arrays <c>[ts, o, h, l, c, vol, volCcy, volCcyQuote,
    /// confirm]</c>, NEWEST FIRST. <c>history-candles</c> rather than <c>candles</c>: the history
    /// route reaches back to 2020, measured.</summary>
    internal Task<IReadOnlyList<string[]>> GetCandles1mAsync(
        string instId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<string[]>(
            $"{_baseUrl}/api/v5/market/history-candles?instId={Uri.EscapeDataString(instId)}"
            + $"&bar=1m&limit=100&before={Ms(from)}&after={Ms(to)}", ct);

    internal Task<IReadOnlyList<OkxFundingHistoryRow>> GetFundingHistoryAsync(
        string instId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<OkxFundingHistoryRow>(
            $"{_baseUrl}/api/v5/public/funding-rate-history?instId={Uri.EscapeDataString(instId)}"
            + $"&limit=100&before={Ms(from)}&after={Ms(to)}", ct);

    private static string Ms(DateTimeOffset at) =>
        at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private async Task<IReadOnlyList<T>> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();

        var envelope = await response.Content.ReadFromJsonAsync<OkxEnvelope<T>>(Json, ct)
                       ?? throw new InvalidOperationException($"OKX returned an empty body for {url}");

        if (!string.Equals(envelope.Code, "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"OKX answered code {envelope.Code} ({envelope.Msg}) for {url}");
        }

        return envelope.Data ?? [];
    }
}

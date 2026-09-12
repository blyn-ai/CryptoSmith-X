using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.CoinbaseIntx;

/// <summary>
/// Thin HTTP over Coinbase International Exchange's public v1 REST. Unauthenticated, no retry.
///
/// <b>This is a different venue from Coinbase, not a segment of it.</b> INTX is a separate legal
/// entity on <c>api.international.coinbase.com</c>, and the budgets are separate too — measured:
/// Advanced Trade was saturated to <c>{429: 60, 200: 20}</c> and the same egress immediately got
/// 12×200 from INTX and 12×200 from Coinbase Exchange. That is why it carries its own
/// <c>exchange</c> row rather than sharing Coinbase's request budget.
///
/// <b>No envelope.</b> v1 returns bare JSON and signals failure with the status code.
/// </summary>
public sealed class CoinbaseIntxClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public CoinbaseIntxClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public CoinbaseIntxClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// The whole venue in ONE call — specification and live quote together. Measured: 310 rows of
    /// which 264 are perpetuals, 451 KB, and each row carries its own <c>quote</c> block with both
    /// sides of the top of book, their sizes, index, mark and the frame's own timestamp.
    ///
    /// Used by BOTH the discovery and the snapshot pass, because splitting it would be two requests
    /// for one response.
    /// </summary>
    internal Task<IReadOnlyList<IntxInstrument>> GetInstrumentsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<IntxInstrument>>($"{_baseUrl}/api/v1/instruments", ct);

    /// <summary>Closed 1-minute bars, newest first, under an <c>aggregations</c> key. The window is
    /// an ISO instant rather than an epoch, like everything else on this venue.</summary>
    internal Task<IntxCandles> GetCandles1mAsync(string symbol, DateTimeOffset from, CancellationToken ct) =>
        GetAsync<IntxCandles>(
            $"{_baseUrl}/api/v1/instruments/{Uri.EscapeDataString(symbol)}/candles"
            + $"?granularity=ONE_MINUTE&start={Iso(from)}", ct);

    /// <summary>The REALISED funding series — the one the ticker's <c>predicted_funding</c> is not.
    /// A call per instrument, which is why it is only ever reached from the funding collector's own
    /// per-symbol loop and never from the snapshot pass.</summary>
    internal Task<IntxFundingPage> GetFundingAsync(string symbol, CancellationToken ct) =>
        GetAsync<IntxFundingPage>(
            $"{_baseUrl}/api/v1/instruments/{Uri.EscapeDataString(symbol)}/funding?result_limit=100", ct);

    private static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException($"Coinbase INTX returned an empty body for {url}");
    }
}

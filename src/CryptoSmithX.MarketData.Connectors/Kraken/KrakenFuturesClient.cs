using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

/// <summary>
/// Thin HTTP over Kraken Futures' public REST. The service holds no keys and places no orders, so
/// every call is unauthenticated. Deliberately dumb: no retry, no logging, no back-off — a
/// non-success status throws and the collector loop above counts and records it. <c>base_url</c> and
/// <c>charts_url</c> come from the database (<c>exchange</c> row) via the ctor: the 1-minute candles
/// live on a different host and path from the derivatives API, which is why there are two bases.
/// </summary>
public sealed class KrakenFuturesClient
{
    private static readonly HttpClient Shared = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _chartsUrl;

    public KrakenFuturesClient(string baseUrl, string chartsUrl)
        : this(Shared, baseUrl, chartsUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler, so the HTTP + JSON +
    /// mapping path is exercised end-to-end without a network.</summary>
    public KrakenFuturesClient(HttpClient http, string baseUrl, string chartsUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _chartsUrl = chartsUrl.TrimEnd('/');
    }

    /// <summary>Every instrument, with each one's raw JSON captured for <c>raw_json</c>.</summary>
    internal async Task<IReadOnlyList<KrakenInstrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/derivatives/api/v3/instruments", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var list = new List<KrakenInstrument>();
        if (doc.RootElement.TryGetProperty("instruments", out var array))
        {
            foreach (var element in array.EnumerateArray())
            {
                var dto = element.Deserialize<KrakenInstrument>(Json)!;
                list.Add(dto with { RawJson = element.GetRawText() });
            }
        }

        return list;
    }

    internal Task<KrakenTickersResponse> GetTickersAsync(CancellationToken ct) =>
        GetAsync<KrakenTickersResponse>($"{_baseUrl}/derivatives/api/v3/tickers", ct);

    internal Task<KrakenCandlesResponse> GetCandles1mAsync(string symbol, long fromSec, long toSec, CancellationToken ct) =>
        GetAsync<KrakenCandlesResponse>($"{_chartsUrl}/trade/{symbol}/1m?from={fromSec}&to={toSec}", ct);

    // v4, not the v3 the rest of the API uses: the v3 historicalfundingrates path 404s for flexible
    // (PF_) futures, while v4 serves them and returns relativeFundingRate (fraction per interval)
    // directly. The endpoint takes no time bounds, so the adapter fetches the series and windows it.
    internal Task<KrakenFundingResponse> GetFundingHistoryAsync(string symbol, CancellationToken ct) =>
        GetAsync<KrakenFundingResponse>($"{_baseUrl}/derivatives/api/v4/historicalfundingrates?symbol={symbol}", ct);

    internal Task<KrakenOrderBookResponse> GetOrderBookAsync(string symbol, CancellationToken ct) =>
        GetAsync<KrakenOrderBookResponse>($"{_baseUrl}/derivatives/api/v3/orderbook?symbol={symbol}", ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new InvalidOperationException($"Kraken returned an empty body for {url}");
    }
}

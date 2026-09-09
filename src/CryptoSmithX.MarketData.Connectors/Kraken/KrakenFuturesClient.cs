using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Connectors.Http;

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
    // Configured centrally — gzip and a pool lifetime that survives this codebase's own pass
    // cadence, neither of which a bare `new HttpClient()` carries; see VenueHttp's own doc
    // comment for what each one measured. One instance shared across all four venue clients:
    // SocketsHttpHandler already pools per origin, so this is one settings object, not one pool.
    private static readonly HttpClient Shared = VenueHttp.Shared;
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
        response.EnsureVenueSuccess();

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

    /// <summary>One of Kraken's analytics series for a symbol — <c>open-interest</c> (OHLC per
    /// bucket) or <c>liquidation-volume</c> (a single number per bucket). Both answer
    /// <c>{"result":{"timestamp":[...],"data":[...]}}</c>, differing only in whether each data
    /// element is an array of four strings or one string; probed live before either was wired.
    /// This is the analytics host, not the derivatives API, so it takes seconds rather than ms.</summary>
    internal Task<KrakenAnalyticsResponse> GetAnalyticsAsync(
        string symbol, string series, long sinceSec, long toSec, int intervalSec, CancellationToken ct) =>
        GetAsync<KrakenAnalyticsResponse>(
            $"{_baseUrl}/api/charts/v1/analytics/{symbol}/{series}?since={sinceSec}&to={toSec}&interval={intervalSec}",
            ct);

    internal Task<KrakenOrderBookResponse> GetOrderBookAsync(string symbol, CancellationToken ct) =>
        GetAsync<KrakenOrderBookResponse>($"{_baseUrl}/derivatives/api/v3/orderbook?symbol={symbol}", ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();

        // Read the body once, as text, because on this venue the status line is not the whole answer.
        // Kraken Futures publishes NO limit for public endpoints — the rate-limit guide says only
        // that public calls have no cost, which is not the same as no ceiling — but both endpoints
        // we call list `apiLimitExceeded` among their errors, and it arrives as an error IN THE BODY
        // of an HTTP 200. Keyed off the status alone, as this method was, a throttled Kraken looked
        // like a symbol with no data: the venue gate was never penalised, the collector never
        // failed, and the pass silently returned nothing. This is the only venue of the four that
        // needs the check, and it is cheap — the token appears nowhere else in these payloads.
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Contains("apiLimitExceeded", StringComparison.Ordinal))
        {
            throw new VenueRateLimitedException($"Kraken answered apiLimitExceeded for {url}", null);
        }

        var value = JsonSerializer.Deserialize<T>(body, Json);
        return value ?? throw new InvalidOperationException($"Kraken returned an empty body for {url}");
    }
}

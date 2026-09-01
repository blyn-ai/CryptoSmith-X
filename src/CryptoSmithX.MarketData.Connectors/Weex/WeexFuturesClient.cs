using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// Thin HTTP over WEEX Futures' public REST. The service holds no keys and places no orders, so every
/// call here is unauthenticated. Deliberately dumb: no retry, no logging, no back-off — a non-success
/// status throws and the collector loop above counts and records it. One host serves both of WEEX's
/// API generations (v2 native, v3 a Binance-format clone); <c>base_url</c> comes from the database
/// (<c>exchange</c> row) via the ctor.
/// </summary>
public sealed class WeexFuturesClient
{
    private static readonly HttpClient Shared = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public WeexFuturesClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler, so the HTTP + JSON +
    /// mapping path is exercised end-to-end without a network.</summary>
    public WeexFuturesClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    internal Task<IReadOnlyList<WeexContract>> GetContractsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<WeexContract>>($"{_baseUrl}/capi/v2/market/contracts", ct);

    internal Task<IReadOnlyList<WeexTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<WeexTicker>>($"{_baseUrl}/capi/v2/market/tickers", ct);

    /// <summary>v3's book ticker (Binance-format symbols) — the only batched source of bid/ask size.</summary>
    internal Task<IReadOnlyList<WeexBookTicker>> GetBookTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<WeexBookTicker>>($"{_baseUrl}/capi/v3/market/ticker/bookTicker", ct);

    /// <summary>Every symbol's current funding rate and interval, in one call.</summary>
    internal Task<IReadOnlyList<WeexFundingRate>> GetCurrentFundingRatesAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<WeexFundingRate>>($"{_baseUrl}/capi/v2/market/currentFundRate", ct);

    /// <summary>Open interest for one symbol — no batch variant exists.</summary>
    internal Task<WeexOpenInterest> GetOpenInterestAsync(string symbol, CancellationToken ct) =>
        GetAsync<WeexOpenInterest>($"{_baseUrl}/capi/v2/market/open_interest?symbol={symbol}", ct);

    /// <summary>Each candle is a string array [time, open, high, low, close, baseVolume, quoteVolume] —
    /// even the timestamp arrives as a quoted string. Order is not guaranteed chronological.</summary>
    internal Task<IReadOnlyList<string[]>> GetCandles1mAsync(string symbol, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<string[]>>(
            $"{_baseUrl}/capi/v2/market/candles?symbol={symbol}&granularity=1m&limit={limit}", ct);

    /// <summary>Newest-first, capped at 100 by the venue with no pagination lever.</summary>
    internal Task<IReadOnlyList<WeexFundingHistoryRow>> GetFundingHistoryAsync(string symbol, CancellationToken ct) =>
        GetAsync<IReadOnlyList<WeexFundingHistoryRow>>(
            $"{_baseUrl}/capi/v2/market/getHistoryFundRate?symbol={symbol}&limit=100", ct);

    internal Task<WeexDepth> GetDepthAsync(string symbol, CancellationToken ct) =>
        GetAsync<WeexDepth>($"{_baseUrl}/capi/v2/market/depth?symbol={symbol}&limit=200", ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new InvalidOperationException($"WEEX returned an empty body for {url}");
    }
}

/// <summary>Raw order book: string [price, qty] pairs per level, bids/asks each already sorted
/// best-first by the venue.</summary>
internal sealed record WeexDepth
{
    public List<string[]> Bids { get; init; } = [];
    public List<string[]> Asks { get; init; } = [];
}

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Bitget;

/// <summary>
/// Thin HTTP over Bitget's public v2 mix REST. Unauthenticated, no retry, no back-off — a failure
/// throws and the collector loop counts it.
///
/// <b>A 200 is not a success here either.</b> v2 signals refusal with a <c>code</c> other than
/// "00000" inside an HTTP 200, so <see cref="GetAsync{T}"/> checks both.
///
/// <b>What the header says and why we ignore it.</b> Responses carry
/// <c>x-mbx-used-remain-limit</c> — a name lifted from Binance — and it is a REMAINING counter that
/// does not move under burst, so a pace cannot be derived from it. Measured instead: forty parallel
/// <c>merge-depth</c> calls returned 40×200 in 1.11 s with no 429 at all. The venue's budget is
/// seeded conservatively for that reason rather than from the header.
/// </summary>
public sealed class BitgetClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The USDT-settled perpetual surface — the segment this adapter is.</summary>
    private const string ProductType = "USDT-FUTURES";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public BitgetClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public BitgetClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    internal Task<IReadOnlyList<BitgetContract>> GetContractsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<BitgetContract>>(
            $"{_baseUrl}/api/v2/mix/market/contracts?productType={ProductType}", ct);

    /// <summary>The whole segment in one response — 787 rows, 396 KB, 0.40 s measured, with the
    /// venue's own <c>ts</c> on every row.</summary>
    internal Task<IReadOnlyList<BitgetTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<BitgetTicker>>(
            $"{_baseUrl}/api/v2/mix/market/tickers?productType={ProductType}", ct);

    /// <summary>
    /// Closed 1-minute bars as string arrays <c>[ms, open, high, low, close, baseVol, quoteVol]</c>,
    /// OLDEST FIRST — the opposite of Bybit's order on the same concept, which is why neither
    /// adapter assumes an order and both key off the bar's own timestamp.
    ///
    /// <c>history-candles</c> rather than <c>candles</c>: the history route reaches back to
    /// 2021-01-02, measured, while the plain one serves a short recent window.
    /// </summary>
    internal Task<IReadOnlyList<string[]>> GetCandles1mAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<IReadOnlyList<string[]>>(
            $"{_baseUrl}/api/v2/mix/market/history-candles?symbol={Uri.EscapeDataString(symbol)}"
            + $"&productType={ProductType}&granularity=1m&limit=200"
            + $"&startTime={Ms(from)}&endTime={Ms(to)}", ct);

    /// <summary>Settled funding, newest first. About ninety days deep, measured — the venue keeps no
    /// more than that on this route.</summary>
    internal Task<IReadOnlyList<BitgetFundingRow>> GetFundingHistoryAsync(
        string symbol, CancellationToken ct) =>
        GetAsync<IReadOnlyList<BitgetFundingRow>>(
            $"{_baseUrl}/api/v2/mix/market/history-fund-rate?symbol={Uri.EscapeDataString(symbol)}"
            + $"&productType={ProductType}&pageSize=100", ct);

    private static string Ms(DateTimeOffset at) =>
        at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();

        var envelope = await response.Content.ReadFromJsonAsync<BitgetEnvelope<T>>(Json, ct)
                       ?? throw new InvalidOperationException($"Bitget returned an empty body for {url}");

        if (!string.Equals(envelope.Code, "00000", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Bitget answered code {envelope.Code} ({envelope.Msg}) for {url}");
        }

        return envelope.Data
               ?? throw new InvalidOperationException($"Bitget returned no data for {url}");
    }
}

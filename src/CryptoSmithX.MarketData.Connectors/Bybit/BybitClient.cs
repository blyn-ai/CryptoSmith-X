using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Bybit;

/// <summary>
/// Thin HTTP over Bybit's public v5 REST. No keys and no orders, so every call here is
/// unauthenticated. Deliberately dumb, like its siblings: no retry, no logging, no back-off — a
/// failure throws and the collector loop counts it.
///
/// <b>A 200 is not a success on this venue.</b> v5 answers a bad request with HTTP 200 and a
/// non-zero <c>retCode</c> in the body. <see cref="GetAsync{T}"/> checks both, so a venue error can
/// never arrive here as an empty list and be written as "the market went quiet".
///
/// <b>What the reconnaissance measured, and what it could not.</b> Public v5 responses carry no
/// <c>X-Bapi-*</c> headers at all — the full header set on a 200 was taken and there is nothing to
/// instrument consumption with, the way <c>BinanceUsdmClient</c> instruments weight. A wall of 900
/// requests in 6.91 s (130 req/s) came back <c>{200: 900}</c>, so the documented 600-per-5-s is not
/// enforced anywhere near our own pace; where the real ceiling is was NOT established, which is why
/// the venue's budget is seeded 'measured' with that caveat rather than 'documented'.
/// </summary>
public sealed class BybitClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Linear USDT/USDC-settled contracts — the segment this adapter is. Perpetuals and
    /// dated futures share it, and the dated ones are dropped by contract type in the adapter
    /// rather than here, because that is a fact about the instrument, not the transport.</summary>
    private const string Category = "linear";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public BybitClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler, so the HTTP + JSON +
    /// mapping path is exercised end to end without a network.</summary>
    public BybitClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Every listed contract's specification. <c>limit=1000</c> against 869 rows measured live, and
    /// the response's own <c>nextPageCursor</c> came back EMPTY — so this is the whole segment in
    /// one call and there is no pagination loop to write. If that ever stops being true the cursor
    /// says so, and the adapter logs rather than silently truncating.
    /// </summary>
    internal Task<BybitList<BybitInstrument>> GetInstrumentsAsync(CancellationToken ct) =>
        GetAsync<BybitList<BybitInstrument>>(
            $"{_baseUrl}/v5/market/instruments-info?category={Category}&limit=1000", ct);

    /// <summary>The whole segment's market in one response — 873 rows, 649 KB, 0.65 s measured. Every
    /// column the snapshot holds for a book venue is on it, so there is nothing to merge.</summary>
    internal Task<BybitList<BybitTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<BybitList<BybitTicker>>($"{_baseUrl}/v5/market/tickers?category={Category}", ct);

    /// <summary>Closed 1-minute bars, newest first, as string arrays
    /// <c>[startMs, open, high, low, close, volumeBase, turnoverQuote]</c>.</summary>
    internal Task<BybitList<string[]>> GetKline1mAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<BybitList<string[]>>(
            $"{_baseUrl}/v5/market/kline?category={Category}&symbol={Uri.EscapeDataString(symbol)}"
            + "&interval=1&limit=1000"
            + $"&start={Ms(from)}&end={Ms(to)}", ct);

    /// <summary>Settled funding, newest first. The venue caps a page at 200 and offers no cursor on
    /// this route, so the window is what bounds it.</summary>
    internal Task<BybitList<BybitFundingRow>> GetFundingHistoryAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<BybitList<BybitFundingRow>>(
            $"{_baseUrl}/v5/market/funding/history?category={Category}&symbol={Uri.EscapeDataString(symbol)}"
            + "&limit=200"
            + $"&startTime={Ms(from)}&endTime={Ms(to)}", ct);

    /// <summary>
    /// The venue's OWN open-interest series — not our periodic observation of a current value, which
    /// is what WEEX and Hyperliquid force. Measured: <c>intervalTime=1h</c> reaches back to
    /// 2021-01-01, the deepest history of anything in the queue.
    /// </summary>
    internal Task<BybitList<BybitOpenInterestRow>> GetOpenInterestAsync(
        string symbol, string intervalTime, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<BybitList<BybitOpenInterestRow>>(
            $"{_baseUrl}/v5/market/open-interest?category={Category}&symbol={Uri.EscapeDataString(symbol)}"
            + $"&intervalTime={intervalTime}&limit=200"
            + $"&startTime={Ms(from)}&endTime={Ms(to)}", ct);

    /// <summary>The book, 200 levels a side — deep enough that the 50 bps band is reached on every
    /// instrument this venue lists, which is the condition for the band being a sum rather than an
    /// undercount.</summary>
    internal Task<BybitOrderBook> GetOrderBookAsync(string symbol, CancellationToken ct) =>
        GetAsync<BybitOrderBook>(
            $"{_baseUrl}/v5/market/orderbook?category={Category}&symbol={Uri.EscapeDataString(symbol)}&limit=200", ct);

    /// <summary>The public tape, newest first, up to a thousand prints. At the depth sweep's own
    /// cadence that covers every instrument this adapter collects — measured against the busiest of
    /// them, which is the only one where it could fail to.</summary>
    internal Task<BybitList<BybitTrade>> GetRecentTradesAsync(string symbol, CancellationToken ct) =>
        GetAsync<BybitList<BybitTrade>>(
            $"{_baseUrl}/v5/market/recent-trade?category={Category}&symbol={Uri.EscapeDataString(symbol)}&limit=1000", ct);

    private static string Ms(DateTimeOffset at) =>
        at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();

        var envelope = await response.Content.ReadFromJsonAsync<BybitEnvelope<T>>(Json, ct)
                       ?? throw new InvalidOperationException($"Bybit returned an empty body for {url}");

        // See the class remarks: this is the check that keeps a venue error from being read as an
        // empty market. The message travels with it because retCode alone is not diagnosable.
        if (envelope.RetCode != 0)
        {
            throw new InvalidOperationException(
                $"Bybit answered retCode {envelope.RetCode} ({envelope.RetMsg}) for {url}");
        }

        return envelope.Result
               ?? throw new InvalidOperationException($"Bybit returned no result for {url}");
    }
}

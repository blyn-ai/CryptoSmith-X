using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Dydx;

/// <summary>
/// Thin HTTP over the dYdX v4 public indexer. No key, no retry — a failure throws and the collector
/// counts it.
///
/// <b>Measured 2026-09-13 against indexer.dydx.trade:</b> the whole venue in one call
/// (<c>perpetualMarkets</c>, 296 markets of which 78 ACTIVE, 167 KB, 0.64 s); the venue states its
/// own limit on every response — <c>ratelimit-limit: 100</c> with a reset about ten seconds out, so
/// 100 requests per ten seconds per address; the book is 100 levels a side and reached about 190
/// bps from the mid on BTC-USD; the tape takes <c>limit=1000</c>, a full day of BTC-USD.
/// </summary>
public sealed class DydxClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public DydxClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public DydxClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    internal Task<DydxMarkets> GetMarketsAsync(CancellationToken ct) =>
        GetAsync<DydxMarkets>($"{_baseUrl}/v4/perpetualMarkets", ct);

    internal Task<DydxBook> GetBookAsync(string ticker, CancellationToken ct) =>
        GetAsync<DydxBook>($"{_baseUrl}/v4/orderbooks/perpetualMarket/{Uri.EscapeDataString(ticker)}", ct);

    /// <summary>The tape, newest first. <paramref name="before"/> pages backwards by time.</summary>
    internal Task<DydxTrades> GetTradesAsync(string ticker, int limit, DateTimeOffset? before, CancellationToken ct) =>
        GetAsync<DydxTrades>(
            $"{_baseUrl}/v4/trades/perpetualMarket/{Uri.EscapeDataString(ticker)}?limit={limit}"
            + (before is { } b ? "&createdBeforeOrAt=" + Uri.EscapeDataString(Iso(b)) : ""), ct);

    /// <summary>Bars, newest first, at most 100 a call.</summary>
    internal Task<DydxCandles> GetCandlesAsync(
        string ticker, string resolution, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct) =>
        GetAsync<DydxCandles>(
            $"{_baseUrl}/v4/candles/perpetualMarkets/{Uri.EscapeDataString(ticker)}?resolution={resolution}"
            + $"&fromISO={Uri.EscapeDataString(Iso(from))}&toISO={Uri.EscapeDataString(Iso(to))}&limit={limit}", ct);

    /// <summary>Settled funding, newest first, one row per hour.</summary>
    internal Task<DydxFundingHistory> GetFundingAsync(string ticker, DateTimeOffset? before, CancellationToken ct) =>
        GetAsync<DydxFundingHistory>(
            $"{_baseUrl}/v4/historicalFunding/{Uri.EscapeDataString(ticker)}?limit=100"
            + (before is { } b ? "&effectiveBeforeOrAt=" + Uri.EscapeDataString(Iso(b)) : ""), ct);

    private static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException($"dYdX returned an empty body for {url}");
    }
}

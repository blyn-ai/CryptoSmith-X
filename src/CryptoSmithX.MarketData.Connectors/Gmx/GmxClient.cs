using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Gmx;

/// <summary>
/// Thin HTTP over GMX's public API for one chain — arbitrum.gmxapi.io, schema at /swagger.json. No key.
///
/// <b>Measured 2026-09-13.</b> The whole venue's tickers in one call (126 markets, 232 KB), the impact
/// parameters in another (/markets/info, 741 KB), 24h volume in a third (/pairs). No published limit and no
/// limit headers: 40 sequential and 60 at 30 parallel all came back 200. The trade search pages at 100 once
/// filtered — asking for more than 300 is an HTTP 400 — behind an offset cursor.
/// </summary>
public sealed class GmxClient
{
    /// <summary>Executed position orders: market and limit increase (2, 3), market, limit and stop-loss
    /// decrease (4, 5, 6), liquidation (7) and stop increase (8). Swaps (0, 1) are left out — they move
    /// pool tokens, not a position, and carry no index price.</summary>
    internal static readonly int[] PositionOrderTypes = [2, 3, 4, 5, 6, 7, 8];

    internal const int Liquidation = 7;

    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _base;

    public GmxClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public GmxClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = baseUrl.TrimEnd('/') + "/v1";
    }

    internal Task<IReadOnlyList<GmxMarket>> GetMarketsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<GmxMarket>>($"{_base}/markets", ct);

    internal Task<IReadOnlyList<GmxTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<GmxTicker>>($"{_base}/markets/tickers", ct);

    internal Task<IReadOnlyList<GmxMarketInfo>> GetMarketsInfoAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<GmxMarketInfo>>($"{_base}/markets/info", ct);

    internal Task<IReadOnlyList<GmxPair>> GetPairsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<GmxPair>>($"{_base}/pairs", ct);

    internal Task<IReadOnlyList<GmxToken>> GetTokensAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<GmxToken>>($"{_base}/tokens", ct);

    /// <summary>Oracle bars, newest first, the latest <paramref name="limit"/> only — the route ignores
    /// <c>since</c> (measured).</summary>
    internal Task<IReadOnlyList<GmxOhlcv>> GetOracleCandlesAsync(string tokenSymbol, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<GmxOhlcv>>(
            $"{_base}/prices/ohlcv?symbol={Uri.EscapeDataString(tokenSymbol)}&timeframe=1m&limit={limit.ToString(CultureInfo.InvariantCulture)}", ct);

    /// <summary>Every account's executions of the given order types, newest first.</summary>
    internal Task<GmxTradesPage> SearchTradesAsync(
        IReadOnlyList<int> orderTypes, string? marketAddress, DateTimeOffset? from, int limit, string? cursor, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["forAllAccounts"] = true,
            ["limit"] = limit,
            ["orderEventCombinations"] = orderTypes.Select(t => new { orderType = t, eventName = "OrderExecuted" }).ToArray(),
        };

        if (marketAddress is not null)
        {
            // The filter needs a direction, so a market is asked for on both.
            body["marketsDirections"] = new[]
            {
                new { marketAddress, direction = "long" },
                new { marketAddress, direction = "short" },
            };
        }

        if (from is { } f)
        {
            body["fromTimestamp"] = f.ToUnixTimeSeconds();
        }

        if (cursor is not null)
        {
            body["cursor"] = cursor;
        }

        return PostAsync<GmxTradesPage>($"{_base}/trades/search", body, ct);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException($"GMX returned an empty body for {url}");
    }

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(url, body, Json, ct);
        response.EnsureVenueSuccess();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException($"GMX returned an empty body for {url}");
    }
}

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// Thin HTTP over Binance SPOT's public <c>/api/v3</c>. Unauthenticated, no retry, no back-off — a
/// failure throws and the collector loop counts it.
///
/// <b>Why this is not <see cref="BinanceUsdmClient"/> with a different base URL.</b> That client is
/// built around <c>/fapi/v1</c>, and the difference is not the prefix: spot has no funding, no open
/// interest, no mark or index price, and it spells the things it does share differently —
/// <c>quoteVolume</c> where futures says <c>quoteVolume</c> on a different row shape, filters as a
/// tagged array rather than named fields, and no onboard date at all. One client covering both
/// would be a type whose half is null depending on which host answered.
///
/// <b>A different HOST is a different budget, and that is measured.</b> api.binance.com allows
/// 6 000 weight a minute where fapi.binance.com allows 2 400 — so the gate is keyed on the host
/// (0054) and this surface does not spend the futures budget. What this client costs, measured
/// 2026-09-13 against the live venue:
///
///     exchangeInfo?showPermissionSets=false   weight 40   6 546 KB   3 698 rows   1.06 s
///     exchangeInfo (full)                     weight 20  17 177 KB   3 698 rows   1.84 s
///     ticker/24hr        (no symbol)          weight 80   1 850 KB   3 701 rows   0.54 s
///     ticker/bookTicker  (no symbol)          weight  4     416 KB   3 701 rows   0.65 s
///
/// The trimmed exchangeInfo costs twice the weight and a third of the bytes; at a discovery cadence
/// of minutes the weight is free and ten megabytes a pass is not, so it is the one used.
/// <c>bookTicker</c> is not called at all — see <see cref="BinanceSpotTicker"/>.
/// </summary>
public sealed class BinanceSpotClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public BinanceSpotClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public BinanceSpotClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Every symbol the venue lists, without the permission sets.
    ///
    /// <c>showPermissionSets=false</c> drops a per-symbol array that is two thirds of the payload
    /// and that nothing here reads — 17.2 MB becomes 6.5 MB, measured. It costs weight 40 instead
    /// of 20, which at this route's cadence is the cheaper half of the trade.
    /// </summary>
    internal async Task<IReadOnlyList<BinanceSpotSymbol>> GetSymbolsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"{_baseUrl}/api/v3/exchangeInfo?showPermissionSets=false", ct);
        response.EnsureVenueSuccess();

        // Read once as a document so each row's own JSON can be kept for the audit column without
        // serialising the parsed shape back — which would record what we understood rather than
        // what the venue said.
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("symbols", out var symbols)
            || symbols.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Binance spot exchangeInfo carried no symbols array");
        }

        var list = new List<BinanceSpotSymbol>(symbols.GetArrayLength());
        foreach (var element in symbols.EnumerateArray())
        {
            var parsed = element.Deserialize<BinanceSpotSymbol>(BinanceJson.Options);
            if (parsed is not null)
            {
                list.Add(parsed with { RawJson = element.GetRawText() });
            }
        }

        return list;
    }

    /// <summary>The whole venue's rolling-window figures AND its live top of book, in one call.</summary>
    internal Task<IReadOnlyList<BinanceSpotTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<BinanceSpotTicker>>($"{_baseUrl}/api/v3/ticker/24hr", ct);

    /// <summary>Closed 1-minute bars, oldest first, in the same twelve-element array futures uses.</summary>
    internal Task<IReadOnlyList<JsonElement[]>> GetCandles1mAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<JsonElement[]>>(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{_baseUrl}/api/v3/klines?symbol={symbol}&interval=1m&limit={limit}"
                + $"&startTime={from.ToUnixTimeMilliseconds()}&endTime={to.ToUnixTimeMilliseconds()}"),
            ct);

    /// <summary>The book for one symbol.</summary>
    internal Task<BinanceSpotDepth> GetDepthAsync(string symbol, int limit, CancellationToken ct) =>
        GetAsync<BinanceSpotDepth>(
            string.Create(CultureInfo.InvariantCulture, $"{_baseUrl}/api/v3/depth?symbol={symbol}&limit={limit}"),
            ct);

    /// <summary>
    /// The public tape, oldest first.
    ///
    /// <c>aggTrades</c> rather than <c>trades</c>: this route folds the fills of one taker order at
    /// one price into a single print, which is the execution as the market experienced it, and it is
    /// what the futures side already stores. Reading both routes into one column would put two
    /// different countings of the same tape under one name.
    /// </summary>
    internal Task<IReadOnlyList<BinanceAggTrade>> GetAggTradesAsync(
        string symbol, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<BinanceAggTrade>>(
            string.Create(CultureInfo.InvariantCulture, $"{_baseUrl}/api/v3/aggTrades?symbol={symbol}&limit={limit}"),
            ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();
        var value = await response.Content.ReadFromJsonAsync<T>(BinanceJson.Options, ct);
        return value ?? throw new InvalidOperationException($"Binance spot returned an empty body for {url}");
    }
}

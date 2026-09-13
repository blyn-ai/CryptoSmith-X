using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Nado;

/// <summary>
/// Thin HTTP over Nado's two public surfaces: the ARCHIVE (indexer; contracts, trades, candles, funding,
/// events) and the GATEWAY (sequencer; symbols, the book). No key.
///
/// <b>Two hosts, one base URL.</b> The segment's base_url names the archive, which carries most of the
/// reads; the gateway is its documented sibling — docs.nado.xyz lists the pairs as
/// archive.prod / gateway.prod and archive.test / gateway.test — so the gateway host is the archive host
/// with its first label swapped, and a test deployment follows along without a second column.
///
/// <b>Measured 2026-09-13:</b> 82 contracts in one <c>/v2/contracts</c> call (8 KB, 0.40 s); the book is 100
/// levels a side at most and reached about 38 bps on BTC; limits are published per query as IP weight —
/// the book at weight 1 against 2 400 a minute, perp prices and funding at weight 2 against 1 200.
/// </summary>
public sealed class NadoClient
{
    private const string UserAgent = "CryptoSmithX-marketdata/1.0 (+https://blynai.eu)";

    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _archive;
    private readonly string _gateway;

    public NadoClient(string archiveBaseUrl)
        : this(Shared, archiveBaseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public NadoClient(HttpClient http, string archiveBaseUrl)
    {
        _http = http;
        _archive = archiveBaseUrl.TrimEnd('/');

        var uri = new Uri(_archive);
        var host = uri.Host.StartsWith("archive.", StringComparison.OrdinalIgnoreCase)
            ? "gateway." + uri.Host["archive.".Length..]
            : uri.Host;
        _gateway = $"{uri.Scheme}://{host}";
    }

    internal Task<IReadOnlyDictionary<string, NadoContract>> GetContractsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyDictionary<string, NadoContract>>($"{_archive}/v2/contracts", ct);

    internal Task<NadoSymbolsEnvelope> GetSymbolsAsync(CancellationToken ct) =>
        GetAsync<NadoSymbolsEnvelope>($"{_gateway}/v1/query?type=symbols", ct);

    internal Task<NadoBook> GetBookAsync(string tickerId, CancellationToken ct) =>
        GetAsync<NadoBook>($"{_gateway}/v2/orderbook?ticker_id={Uri.EscapeDataString(tickerId)}&depth=100", ct);

    internal Task<IReadOnlyList<NadoTrade>> GetTradesAsync(string tickerId, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<NadoTrade>>($"{_archive}/v2/trades?ticker_id={Uri.EscapeDataString(tickerId)}&limit={limit}", ct);

    internal Task<NadoCandles> GetCandlesAsync(int productId, DateTimeOffset to, int limit, CancellationToken ct) =>
        PostAsync<NadoCandles>(new { candlesticks = new { product_id = productId, granularity = 60, max_time = to.ToUnixTimeSeconds(), limit } }, ct);

    internal Task<NadoFundingHistory> GetFundingHistoryAsync(int productId, DateTimeOffset from, int limit, CancellationToken ct) =>
        PostAsync<NadoFundingHistory>(new { funding_rate_history = new { product_id = productId, start_time = from.ToUnixTimeSeconds(), limit } }, ct);

    /// <summary>Liquidation events on one product, newest first, walking back from <paramref name="maxTime"/>.</summary>
    internal Task<NadoEvents> GetLiquidationsAsync(int productId, DateTimeOffset? maxTime, int txs, CancellationToken ct) =>
        maxTime is { } mt
            ? PostAsync<NadoEvents>(new { events = new { product_ids = new[] { productId }, event_types = new[] { "liquidate_subaccount" }, max_time = mt.ToUnixTimeSeconds(), limit = new { txs } } }, ct)
            : PostAsync<NadoEvents>(new { events = new { product_ids = new[] { productId }, event_types = new[] { "liquidate_subaccount" }, limit = new { txs } } }, ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureVenueSuccess();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException($"Nado returned an empty body for {url}");
    }

    private async Task<T> PostAsync<T>(object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_archive}/v1") { Content = JsonContent.Create(body, options: Json) };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureVenueSuccess();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException("Nado archive returned an empty body");
    }
}

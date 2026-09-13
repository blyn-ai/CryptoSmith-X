using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Synthetix;

/// <summary>
/// Thin HTTP over Synthetix's public info endpoint. Every read is a POST to one URL with an
/// <c>action</c> in the body; no key is needed for any of them.
///
/// <b>Measured 2026-09-13 against papi.synthetix.io:</b> ten markets, all quoted in USDT;
/// <c>getMarketPrices</c> answers for the whole venue in one call; the book was 12 and 11 levels deep
/// on BTC-USDT and still reached about 200 bps from the mid. The documented limit is 1 000 requests a
/// minute per IP with bursts to 100 a second, and a refusal is HTTP 429 with
/// <c>RATE_LIMIT_EXCEEDED</c>, which the pacing already reads.
///
/// <b>The venue requires a descriptive User-Agent on every request</b> (its Environments page), so
/// this client names itself rather than sending the runtime's default.
/// </summary>
public sealed class SynthetixClient
{
    private const string UserAgent = "CryptoSmithX-marketdata/1.0 (+https://blynai.eu)";

    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _infoUrl;

    public SynthetixClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public SynthetixClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _infoUrl = baseUrl.TrimEnd('/') + "/v1/info";
    }

    internal Task<IReadOnlyList<SynthetixMarket>> GetMarketsAsync(CancellationToken ct) =>
        InfoAsync<IReadOnlyList<SynthetixMarket>>(new { action = "getMarkets" }, ct);

    internal Task<IReadOnlyDictionary<string, SynthetixPrice>> GetPricesAsync(CancellationToken ct) =>
        InfoAsync<IReadOnlyDictionary<string, SynthetixPrice>>(new { action = "getMarketPrices" }, ct);

    internal Task<SynthetixFundingRate> GetFundingRateAsync(string symbol, CancellationToken ct) =>
        InfoAsync<SynthetixFundingRate>(new { action = "getFundingRate", symbol }, ct);

    /// <summary>The deepest book the venue serves; it is shallow enough that the limit is not what
    /// bounds it.</summary>
    internal Task<SynthetixBook> GetBookAsync(string symbol, CancellationToken ct) =>
        InfoAsync<SynthetixBook>(new { action = "getOrderbook", symbol, limit = 1000 }, ct);

    /// <summary>The newest prints, newest first; 100 is the route's maximum.</summary>
    internal Task<SynthetixTrades> GetTradesAsync(string symbol, CancellationToken ct) =>
        InfoAsync<SynthetixTrades>(new { action = "getLastTrades", symbol, limit = 100 }, ct);

    internal Task<SynthetixCandles> GetCandlesAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        InfoAsync<SynthetixCandles>(
            new { action = "getCandles", symbol, interval = "1m",
                  startTime = from.ToUnixTimeMilliseconds(), endTime = to.ToUnixTimeMilliseconds() }, ct);

    internal Task<SynthetixFundingHistory> GetFundingHistoryAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        InfoAsync<SynthetixFundingHistory>(
            new { action = "getFundingRateHistory", symbol,
                  startTime = from.ToUnixTimeMilliseconds(), endTime = to.ToUnixTimeMilliseconds() }, ct);

    private async Task<T> InfoAsync<T>(object parameters, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _infoUrl)
        {
            Content = JsonContent.Create(new { @params = parameters }, options: Json),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureVenueSuccess();

        var envelope = await response.Content.ReadFromJsonAsync<SynthetixEnvelope<T>>(Json, ct)
                       ?? throw new InvalidOperationException("Synthetix returned an empty body");

        if (!string.Equals(envelope.Status, "ok", StringComparison.Ordinal) || envelope.Response is null)
        {
            throw new InvalidOperationException(
                $"Synthetix answered status '{envelope.Status}' ({envelope.Error?.Code}: {envelope.Error?.Message})");
        }

        return envelope.Response;
    }
}

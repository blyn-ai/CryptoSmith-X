using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Mexc;

/// <summary>
/// Thin HTTP over MEXC's public contract REST. Unauthenticated, no retry.
///
/// <b>THE THROTTLE ARRIVES AS AN HTTP 200, AND THAT IS THE WHOLE REASON THIS CLASS IS DIFFERENT.</b>
/// The venue refuses with <c>{"success":false,"code":510,"message":"Requests are too frequent"}</c>
/// under a 200 status line. Every pacing mechanism in this codebase keys off a 429:
/// <c>VenueGate.Penalize()</c> is documented as the reaction to one, <c>VenuePenalty.Apply</c>
/// switches on one, and the collectors call it with whatever the client threw. So a 510 read as a
/// success would mean this venue can NEVER slow us down — we would simply keep asking at a rate it
/// has already refused.
///
/// The translation is one line and it is deliberately <see cref="VenueRateLimitedException"/> rather
/// than a MEXC-specific type: that exception already carries a 429 status, so every existing pacing
/// path — the gate, the penalty, the collector's own counting — works on this venue without knowing
/// it exists.
///
/// <b>The documented rate is not the real one, measured.</b> 120 requests offered at the published
/// 10/s gave <c>{(200, code 0): 60, (200, code 510): 60}</c> — exactly half refused at the rate the
/// documentation states. The same endpoint at about 2.5/s went 20 for 20 clean. The venue's budget
/// is seeded from the measurement, not the documentation.
///
/// <b>And the reason this adapter reads only bulk routes.</b> The whole segment's market is two
/// calls — <c>contract/ticker</c> and <c>contract/detail</c> — plus one for funding. At the
/// snapshot's own cadence that is three requests a minute against a venue whose measured ceiling is
/// around 2.5 per second, which is the safest possible way to meet a host the reconnaissance warned
/// might ban an IP outright.
/// </summary>
public sealed class MexcClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The same options with case-insensitive matching turned OFF, for the one route that needs it.
    ///
    /// <c>contract/deals</c> sends BOTH <c>"T"</c> (the side, 1 or 2) and <c>"t"</c> (the timestamp).
    /// Under the web defaults those two names are the same name, and the serializer refuses the type
    /// outright — "The JSON property name for MexcDeal.t collides with another property" — at the
    /// first response, which took the book and the tape down together on this venue.
    ///
    /// Safe to read this route strictly because every property on the shapes it touches carries an
    /// explicit <see cref="JsonPropertyNameAttribute"/> spelled exactly as the venue sends it.
    /// </summary>
    private static readonly JsonSerializerOptions CaseSensitiveJson =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    /// <summary>The venue's own "too frequent" code, delivered under a 200.</summary>
    private const int TooFrequent = 510;

    /// <summary>How long to park the venue when it says 510. The reconnaissance saw recovery in
    /// 30 s to 3 min; this is the conservative end of that, because the cost of asking again too
    /// early on this particular venue may be the whole host.</summary>
    private static readonly TimeSpan TooFrequentCooldown = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public MexcClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public MexcClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    internal Task<IReadOnlyList<MexcContract>> GetContractsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<MexcContract>>($"{_baseUrl}/api/v1/contract/detail", ct);

    internal Task<IReadOnlyList<MexcTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<MexcTicker>>($"{_baseUrl}/api/v1/contract/ticker", ct);

    /// <summary>Funding for every symbol in one call — and the only place this venue states its
    /// funding INTERVAL, since <c>contract/detail</c>'s own field is null on all 1 192 contracts.</summary>
    internal Task<IReadOnlyList<MexcFundingRow>> GetFundingRatesAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<MexcFundingRow>>($"{_baseUrl}/api/v1/contract/funding_rate", ct);

    /// <summary>
    /// SETTLED funding, one page at a time, newest first.
    ///
    /// The first draft of this adapter reported that MEXC publishes no funding series and stored its
    /// own reading of the current rate instead. It does publish one: 1 618 payments deep on BTC_USDT,
    /// measured, back to the contract's listing. Paginated — <c>totalPage</c> says how far it goes —
    /// so the caller walks pages until the rows fall behind the window it asked for.
    /// </summary>
    internal Task<MexcFundingPage> GetFundingHistoryAsync(
        string symbol, int page, int pageSize, CancellationToken ct) =>
        GetAsync<MexcFundingPage>(
            $"{_baseUrl}/api/v1/contract/funding_rate/history?symbol={Uri.EscapeDataString(symbol)}"
            + $"&page_num={page}&page_size={pageSize}", ct);

    /// <summary>Column-oriented candles: parallel arrays under one object, timestamps in SECONDS.
    /// Measured: minute bars live about a month, and are gone at T−60d.</summary>
    internal Task<MexcKline> GetCandles1mAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<MexcKline>(
            $"{_baseUrl}/api/v1/contract/kline/{Uri.EscapeDataString(symbol)}"
            + $"?interval=Min1&start={Sec(from)}&end={Sec(to)}", ct);

    /// <summary>The book. One call per collected symbol on the depth sweep — the route the
    /// reconnaissance measured, and the pace it measured at (about 2.5/s) is orders of magnitude
    /// above what this adapter asks of it.</summary>
    internal Task<MexcDepth> GetDepthAsync(string symbol, CancellationToken ct) =>
        GetAsync<MexcDepth>($"{_baseUrl}/api/v1/contract/depth/{Uri.EscapeDataString(symbol)}", ct);

    /// <summary>The public tape, newest first.</summary>
    internal Task<IReadOnlyList<MexcDeal>> GetDealsAsync(string symbol, CancellationToken ct) =>
        GetAsync<IReadOnlyList<MexcDeal>>(
            $"{_baseUrl}/api/v1/contract/deals/{Uri.EscapeDataString(symbol)}", ct, CaseSensitiveJson);

    private static string Sec(DateTimeOffset at) =>
        at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct, JsonSerializerOptions? json = null)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();

        var envelope = await response.Content.ReadFromJsonAsync<MexcEnvelope<T>>(json ?? Json, ct)
                       ?? throw new InvalidOperationException($"MEXC returned an empty body for {url}");

        // See the class remarks: this is the whole point of the class.
        if (envelope.Code == TooFrequent)
        {
            throw new VenueRateLimitedException(
                $"MEXC refused {url} as too frequent (code 510, under an HTTP 200)", TooFrequentCooldown);
        }

        if (!envelope.Success || envelope.Code != 0)
        {
            throw new InvalidOperationException(
                $"MEXC answered code {envelope.Code} ({envelope.Message}) for {url}");
        }

        return envelope.Data
               ?? throw new InvalidOperationException($"MEXC returned no data for {url}");
    }
}

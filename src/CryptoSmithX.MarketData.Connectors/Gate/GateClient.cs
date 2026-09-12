using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Http;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Gate;

/// <summary>
/// Thin HTTP over Gate's public v4 futures REST. Unauthenticated, no retry, no back-off.
///
/// <b>No envelope, unlike its two neighbours.</b> v4 answers with a bare array and puts failure in
/// the HTTP status, so <see cref="EnsureVenueSuccess"/> is the whole check.
///
/// <b>The rate-limit headers lie by a factor of ten, and it was reproduced.</b> Three interleaved
/// calls in one window returned remaining counts of 199, 199, 199, then 198, then 197 — a SEPARATE
/// counter per URL, with <c>x-gate-ratelimit-reset-timestamp</c> equal to the current second every
/// time. Read the header alone and you would write "200 per second"; the honest reading is 200 per
/// ten seconds PER ENDPOINT, which maps to about 20 req/s on our single scalar. The venue is
/// therefore deliberately under-used, and this comment exists so nobody later "fixes" that.
///
/// <b>Minute candles live one week.</b> Measured: a 600-second window at T−6d returns bars; the same
/// window at T−7d returns <c>Candlestick too long ago. Maximum 10000 points recently are allowed</c>.
/// This is the only venue in the queue where even minute OHLCV is unrecoverable after a week — an
/// argument for collecting it now, not an argument against the venue.
/// </summary>
public sealed class GateClient
{
    private static readonly HttpClient Shared = VenueHttp.Shared;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The USDT-settled futures surface — the segment this adapter is.</summary>
    private const string Settle = "usdt";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public GateClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler.</summary>
    public GateClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    internal Task<IReadOnlyList<GateContract>> GetContractsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<GateContract>>($"{_baseUrl}/api/v4/futures/{Settle}/contracts", ct);

    /// <summary>981 rows, 461 KB measured, with every column the snapshot holds filled.</summary>
    internal Task<IReadOnlyList<GateTicker>> GetTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<GateTicker>>($"{_baseUrl}/api/v4/futures/{Settle}/tickers", ct);

    /// <summary>Closed 1-minute bars, oldest first. Seconds on the wire, not milliseconds.</summary>
    internal Task<IReadOnlyList<GateCandle>> GetCandles1mAsync(
        string contract, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetAsync<IReadOnlyList<GateCandle>>(
            $"{_baseUrl}/api/v4/futures/{Settle}/candlesticks?contract={Uri.EscapeDataString(contract)}"
            + $"&interval=1m&from={Sec(from)}&to={Sec(to)}", ct);

    /// <summary>Settled funding, newest first, about ninety records deep.</summary>
    internal Task<IReadOnlyList<GateFundingRow>> GetFundingHistoryAsync(
        string contract, CancellationToken ct) =>
        GetAsync<IReadOnlyList<GateFundingRow>>(
            $"{_baseUrl}/api/v4/futures/{Settle}/funding_rate?contract={Uri.EscapeDataString(contract)}&limit=100", ct);

    /// <summary>
    /// The book, three hundred levels a side — the deepest this route accepts; 400 is a 400.
    ///
    /// <b>The claim that used to stand here was wrong.</b> It said fifty levels reach past the 50 bps
    /// band on every contract sampled. On BTC_USDT fifty levels reach about 3 bps and a hundred
    /// reach 5.6, measured, so every depth column was empty on the venue's most traded contract.
    /// Three hundred reach 16 bps.
    ///
    /// The route also takes an <c>interval</c> that aggregates the ladder onto a coarser price grid
    /// and buys far more span — 300 levels at interval=5 reach 194 bps on BTC_USDT. It is not used,
    /// because interval is an ABSOLUTE price step: the value that turns BTC's ladder into something
    /// useful would swallow an altcoin priced under a dollar whole, and a per-instrument value would
    /// have to be derived from a set of steps the venue accepts but does not publish (it takes 1 and
    /// 5 and refuses 2). Span bought that way would cost the band edges their meaning.
    /// </summary>
    internal Task<GateOrderBook> GetOrderBookAsync(string contract, CancellationToken ct) =>
        GetAsync<GateOrderBook>(
            $"{_baseUrl}/api/v4/futures/{Settle}/order_book?contract={Uri.EscapeDataString(contract)}&limit=300", ct);

    /// <summary>The public tape, newest first.</summary>
    internal Task<IReadOnlyList<GateTrade>> GetTradesAsync(string contract, CancellationToken ct) =>
        GetAsync<IReadOnlyList<GateTrade>>(
            $"{_baseUrl}/api/v4/futures/{Settle}/trades?contract={Uri.EscapeDataString(contract)}&limit=100", ct);

    /// <summary>
    /// The venue's own hourly statistics: open interest AND the period's liquidated size on each
    /// side. Two datasets from one call, which is the only reason this venue has an open-interest
    /// history at all — the plain OI routes elsewhere give a current number and nothing else.
    /// </summary>
    internal Task<IReadOnlyList<GateContractStat>> GetContractStatsAsync(
        string contract, DateTimeOffset from, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<GateContractStat>>(
            $"{_baseUrl}/api/v4/futures/{Settle}/contract_stats?contract={Uri.EscapeDataString(contract)}"
            + $"&interval=1h&limit={limit}&from={Sec(from)}", ct);

    private static string Sec(DateTimeOffset at) =>
        at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureVenueSuccess();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new InvalidOperationException($"Gate returned an empty body for {url}");
    }
}

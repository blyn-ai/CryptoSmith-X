using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// Thin HTTP over Binance USDⓈ-M Futures' public REST (<c>/fapi/v1</c>). The service holds no keys and
/// places no orders, so every call is unauthenticated. Deliberately dumb, exactly like its Kraken and
/// WEEX siblings: no retry, no logging, no back-off — a non-success status throws and the collector
/// loop above counts and records it. <c>base_url</c> comes from the database (<c>segment</c> row) via
/// the ctor; one host serves everything, so there is no second base like Kraken's charts URL.
///
/// WEIGHTS, because on this venue they are the whole design constraint. Binance meters a per-IP
/// budget of 2400 weight per minute (published in <c>exchangeInfo.rateLimits</c> and read live) and
/// charges each endpoint differently, so "how many requests per second" is not the question the venue
/// is actually asking. Every number below was measured today from the <c>x-mbx-used-weight-1m</c>
/// response header, not copied from documentation:
///
///     exchangeInfo                       1        fundingInfo                    0 (no header)
///     ticker/bookTicker  (no symbol)     5        premiumIndex   (no symbol)     6  (docs say 10)
///     ticker/24hr        (no symbol)    40        openInterest   (per symbol)    1
///     klines  limit&lt;=100               1        klines  limit=1500            10
///     depth   limit=100                  5        depth   limit=500             10
///     fundingRate                        0 (no header)
///
/// The consequence worth carrying in your head: a full-venue snapshot is 5 + 10 + 40 = 55 weight in
/// three calls, i.e. affordable several times a minute, while the order book is per symbol and
/// nothing about it is affordable — which is what migration 0023 argues about and what the WebSocket
/// feed exists to fix.
/// </summary>
public sealed class BinanceUsdmClient
{
    /// <summary>
    /// The depth the per-symbol REST book asks for, and a measured compromise rather than a taste.
    /// <see cref="Kraken.DepthMath"/> nulls any band the book does not reach past, so the only limit
    /// worth paying for is one that bounds the 10/25/50 bps bands. Measured live today, distance from
    /// mid to the deepest returned level:
    ///
    ///     limit=50  (weight 2)   BTC 0.7 bps   ETH  2.0 bps   DOGE   55 bps   IOTA  117 bps
    ///     limit=100 (weight 5)   BTC 1.4 bps   ETH  4.0 bps   DOGE  111 bps   IOTA  262 bps
    ///     limit=500 (weight 10)  BTC 7.3 bps   ETH 21.0 bps   DOGE  555 bps   IOTA 2668 bps
    ///     limit=1000(weight 20)  BTC 17.2 bps  ETH 46.2 bps   DOGE 1112 bps   IOTA 9977 bps
    ///
    /// 100 bounds all three bands on everything except BTC and ETH; going to 500 buys ETH's 10 bps
    /// band alone for twice the weight, and 1000 buys BTC's 10 bps band for four times the weight and
    /// still cannot bound BTC at 25 bps. Paying 4x across ~570 symbols to fill two cells is not a
    /// trade worth making on a metered budget. BTC's and ETH's deep bands are honestly null over
    /// REST; the WebSocket book, which maintains every level the venue publishes rather than a
    /// window of them, is what fills them in.
    /// </summary>
    public const int DepthLimit = 100;

    /// <summary>
    /// The depth a WebSocket book is SEEDED from, where the arithmetic is different and so is the
    /// answer. A seed is paid once per symbol per connection rather than once per sweep, and it fixes
    /// how far the maintained book can ever be trusted: the diff stream reports levels that CHANGED,
    /// so a level that was outside the seed and has not traded since is simply absent, and a band
    /// summed across it would be an undercount wearing a real number's clothes. Buying the venue's
    /// deepest available window once is therefore worth 20 weight in a way that buying it every sweep
    /// is not. See <see cref="BinanceBookBuilder"/>, which records the seeded reach and refuses to
    /// answer any band beyond it.
    /// </summary>
    public const int SeedDepthLimit = 1000;

    /// <summary>
    /// Rows per klines call. The venue allows 1500, but the weight is charged on the LIMIT ASKED FOR
    /// and not on the rows returned, stepping 1 → 2 → 5 → 10 at 100 / 500 / 1000 / above (measured:
    /// limit=100 costs 1, limit=1500 costs 10). Steady state wants one or two bars, so anything above
    /// 100 would be paying double or more for headroom that is never used; a long outage still closes
    /// itself, just over several passes, which is exactly how <c>CandleCollector</c> already
    /// backfills — it re-asks from the newest stored bar every pass. Across ~570 symbols on a 60 s
    /// candle interval this is the difference between 570 and 1140 weight per minute against a
    /// 2400/minute budget, i.e. between a quarter of the venue and a half of it.
    /// </summary>
    private const int KlineLimit = 100;

    private static readonly HttpClient Shared = new();

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public BinanceUsdmClient(string baseUrl)
        : this(Shared, baseUrl)
    {
    }

    /// <summary>For tests: an <see cref="HttpClient"/> over a stub handler, so the HTTP + JSON +
    /// mapping path is exercised end-to-end without a network.</summary>
    public BinanceUsdmClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>Every listing, with each one's raw JSON captured for <c>raw_json</c> — read off the
    /// source element rather than rebuilt from the mapped fields, so the column holds what the venue
    /// said and not what we understood of it.</summary>
    internal async Task<IReadOnlyList<BinanceSymbol>> GetSymbolsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/fapi/v1/exchangeInfo", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var list = new List<BinanceSymbol>();
        if (doc.RootElement.TryGetProperty("symbols", out var array))
        {
            foreach (var element in array.EnumerateArray())
            {
                var dto = element.Deserialize<BinanceSymbol>(BinanceJson.Options)!;
                list.Add(dto with { RawJson = element.GetRawText() });
            }
        }

        return list;
    }

    /// <summary>Funding terms for the symbols whose terms deviate from the venue default. Weight 0.</summary>
    internal Task<IReadOnlyList<BinanceFundingInfo>> GetFundingInfoAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<BinanceFundingInfo>>($"{_baseUrl}/fapi/v1/fundingInfo", ct);

    /// <summary>The whole venue's top of book in one call. Weight 5.</summary>
    internal Task<IReadOnlyList<BinanceBookTicker>> GetBookTickersAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<BinanceBookTicker>>($"{_baseUrl}/fapi/v1/ticker/bookTicker", ct);

    /// <summary>The whole venue's mark price, index price and current funding rate. Weight 10.</summary>
    internal Task<IReadOnlyList<BinancePremiumIndex>> GetPremiumIndexAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<BinancePremiumIndex>>($"{_baseUrl}/fapi/v1/premiumIndex", ct);

    /// <summary>The whole venue's 24 h statistics. Weight 40 — the single most expensive call the
    /// adapter makes, and the only batched source of turnover.</summary>
    internal Task<IReadOnlyList<BinanceTicker24h>> GetTicker24hAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<BinanceTicker24h>>($"{_baseUrl}/fapi/v1/ticker/24hr", ct);

    /// <summary>Open interest for one symbol. Weight 1, and there is no batched form: omitting
    /// <c>symbol</c> answers HTTP 400 <c>-1102</c>, verified live.</summary>
    internal Task<BinanceOpenInterest> GetOpenInterestAsync(string symbol, CancellationToken ct) =>
        GetAsync<BinanceOpenInterest>($"{_baseUrl}/fapi/v1/openInterest?symbol={symbol}", ct);

    /// <summary>Closed 1-minute bars in [from, to]. Each row is a heterogeneous array — the open time
    /// is an unquoted integer, the prices are quoted strings, the trade count is an unquoted integer —
    /// so it is read as elements rather than bound to a typed row.</summary>
    internal Task<IReadOnlyList<JsonElement[]>> GetKlines1mAsync(
        string symbol, long fromMs, long toMs, CancellationToken ct) =>
        GetAsync<IReadOnlyList<JsonElement[]>>(
            $"{_baseUrl}/fapi/v1/klines?symbol={symbol}&interval=1m&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}",
            ct);

    /// <summary>Historical funding payments in [from, to], oldest first. Weight-free.</summary>
    internal Task<IReadOnlyList<BinanceFundingRateRow>> GetFundingHistoryAsync(
        string symbol, long fromMs, long toMs, CancellationToken ct) =>
        GetAsync<IReadOnlyList<BinanceFundingRateRow>>(
            $"{_baseUrl}/fapi/v1/fundingRate?symbol={symbol}&startTime={fromMs}&endTime={toMs}&limit=1000", ct);

    /// <summary>The order book for one symbol. <paramref name="limit"/> is the caller's because the
    /// two callers want different things from it — see <see cref="DepthLimit"/> and
    /// <see cref="SeedDepthLimit"/>.</summary>
    internal Task<BinanceDepth> GetDepthAsync(string symbol, int limit, CancellationToken ct) =>
        GetAsync<BinanceDepth>(
            string.Create(CultureInfo.InvariantCulture, $"{_baseUrl}/fapi/v1/depth?symbol={symbol}&limit={limit}"), ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(BinanceJson.Options, ct);
        return value ?? throw new InvalidOperationException($"Binance returned an empty body for {url}");
    }
}

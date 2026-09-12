using System.Net;
using System.Text;
using CryptoSmithX.MarketData.Connectors.Bitget;
using CryptoSmithX.MarketData.Connectors.Bybit;
using CryptoSmithX.MarketData.Connectors.Gate;
using CryptoSmithX.MarketData.Connectors.Mexc;
using CryptoSmithX.MarketData.Connectors.Okx;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The series these venues publish BESIDE the traded price — mark bars, index bars, the venue's own
/// open-interest history, its settled funding.
///
/// <b>Why this file exists.</b> Every one of these was first reported as "the venue does not publish
/// it". Each report was wrong, and wrong in the same way: the route was looked for under the name
/// the previous venue used, not found, and the absence written into a comment where it then looked
/// settled. The fixtures here are trimmed copies of live responses taken 2026-09-12, so what the
/// venues actually serve is recorded in the repository rather than in a conclusion about them.
///
/// <b>The traps, and they are all the same trap.</b> Mark, index and traded bars have identical
/// shapes and near-identical values — four prices within a few dozen basis points of each other.
/// Ask the wrong route, key the request wrongly, or relabel one series as another, and nothing looks
/// broken: the column fills with plausible prices. So each test below checks the REQUEST as well as
/// the parse, because a correct parse of the wrong series is the failure mode.
/// </summary>
public sealed class MarkAndIndexSeriesTests
{
    private static readonly DateTimeOffset From = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000);
    private static readonly DateTimeOffset To = DateTimeOffset.FromUnixTimeSeconds(1_789_900_000);

    // ── Bybit ────────────────────────────────────────────────────────────────────────────────

    private const string BybitMark = """
        {"retCode":0,"retMsg":"OK","result":{"symbol":"BTCUSDT","category":"linear","list":[
          ["1789242660000","77078.96","77078.96","77077.44","77077.44"],
          ["1789242600000","77082.71","77082.71","77078.96","77078.96"]]},"time":1789242696550}
        """;

    private const string BybitIndex = """
        {"retCode":0,"retMsg":"OK","result":{"symbol":"BTCUSDT","category":"linear","list":[
          ["1789242660000","77116.05","77116.07","77116.03","77116.07"]]},"time":1789242697011}
        """;

    [Fact]
    public async Task Bybits_mark_bars_carry_no_volume_and_none_is_invented_for_them()
    {
        // FIVE fields, not seven: a mark price is a computed reference, so nothing traded at it and
        // there is no volume to report. A zero here would be a measurement claiming the market stood
        // still — which is a different statement from "this series has no such column".
        var seen = new List<string>();
        var bybit = new BybitPerpMarketData(new BybitClient(
            Recording(seen, ("/v5/market/mark-price-kline", BybitMark)), "https://api.test"));

        var bars = await bybit.GetPriceCandles1mAsync("BTCUSDT", "mark", From, To, CancellationToken.None);

        var b = bars.First(x => x.OpenTime == DateTimeOffset.FromUnixTimeMilliseconds(1_789_242_600_000));
        Assert.Equal("mark", b.Series);
        Assert.Equal(77082.71, b.Open, 6);
        Assert.Equal(77082.71, b.High, 6);
        Assert.Equal(77078.96, b.Low, 6);
        Assert.Equal(77078.96, b.Close, 6);

        Assert.Single(seen);
        Assert.Contains("mark-price-kline", seen[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bybits_index_bars_come_from_the_index_route_and_not_the_mark_one()
    {
        // The two differ by about 37 dollars on BTC in this very fixture — a third of a basis point.
        // Serving one from the other's route would be invisible in every column that shows a price
        // and wrong in every column that shows a basis.
        var seen = new List<string>();
        var bybit = new BybitPerpMarketData(new BybitClient(
            Recording(seen, ("/v5/market/index-price-kline", BybitIndex)), "https://api.test"));

        var b = Assert.Single(await bybit.GetPriceCandles1mAsync(
            "BTCUSDT", "index", From, To, CancellationToken.None));

        Assert.Equal("index", b.Series);
        Assert.Equal(77116.07, b.Close, 6);
        Assert.Contains("index-price-kline", Assert.Single(seen), StringComparison.Ordinal);
    }

    // ── Bitget ───────────────────────────────────────────────────────────────────────────────

    private const string BitgetMark = """
        {"code":"00000","msg":"success","requestTime":1789242697415,"data":[
          ["1789242540000","77094.3","77094.4","77084.6","77087.9","0","0"],
          ["1789242600000","77087.9","77087.9","77079.4","77079.5","0","0"]]}
        """;

    [Fact]
    public async Task Bitget_reaches_for_the_history_route_of_the_series_it_was_asked_for()
    {
        var seen = new List<string>();
        var bitget = new BitgetPerpMarketData(new BitgetClient(
            Recording(seen,
                ("/api/v2/mix/market/history-mark-candles", BitgetMark),
                ("/api/v2/mix/market/history-index-candles", BitgetMark)),
            "https://api.test"));

        await bitget.GetPriceCandles1mAsync("BTCUSDT", "mark", From, To, CancellationToken.None);
        await bitget.GetPriceCandles1mAsync("BTCUSDT", "index", From, To, CancellationToken.None);

        Assert.Contains("history-mark-candles", seen[0], StringComparison.Ordinal);
        Assert.Contains("history-index-candles", seen[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bitgets_two_trailing_zeros_are_not_read_as_a_market_that_stopped()
    {
        // This venue pads its mark bars out to the traded shape, so the volume columns are present
        // and always "0". They are not volume — they are filler — and a mark bar carries none.
        var bitget = new BitgetPerpMarketData(new BitgetClient(
            Stub(("/api/v2/mix/market/history-mark-candles", BitgetMark)), "https://api.test"));

        var bars = await bitget.GetPriceCandles1mAsync("BTCUSDT", "mark", From, To, CancellationToken.None);

        var b = bars.First(x => x.OpenTime == DateTimeOffset.FromUnixTimeMilliseconds(1_789_242_540_000));
        Assert.Equal(77094.3, b.Open, 6);
        Assert.Equal(77084.6, b.Low, 6);
    }

    // ── Gate ─────────────────────────────────────────────────────────────────────────────────

    private const string GateMark = """
        [{"o":"77085.71","v":0,"t":1789242600,"c":"77083.25","l":"77083.25","h":"77085.71","sum":"0"}]
        """;

    [Fact]
    public async Task Gate_asks_for_a_series_by_prefixing_the_contract_not_by_changing_the_route()
    {
        // One route serves all three series here; which one you get is decided by the CONTRACT name
        // — "BTC_USDT", "mark_BTC_USDT", "index_BTC_USDT". Drop the prefix and the same route
        // answers 200 with the traded bars, filed under mark.
        var seen = new List<string>();
        var gate = new GatePerpMarketData(new GateClient(
            Recording(seen, ("/api/v4/futures/usdt/candlesticks", GateMark)), "https://api.test"));

        var b = Assert.Single(await gate.GetPriceCandles1mAsync(
            "BTC_USDT", "mark", From, To, CancellationToken.None));

        Assert.Contains("contract=mark_BTC_USDT", Assert.Single(seen), StringComparison.Ordinal);

        // Stored under the INSTRUMENT's own symbol: the prefix is how the venue addresses the
        // series, not a second instrument.
        Assert.Equal("BTC_USDT", b.ExchangeSymbol);
        Assert.Equal("mark", b.Series);
        Assert.Equal(77083.25, b.Close, 6);
    }

    // ── OKX ──────────────────────────────────────────────────────────────────────────────────

    private const string OkxInstruments = """
        {"code":"0","msg":"","data":[
          {"instId":"BTC-USDT-SWAP","instFamily":"BTC-USDT","ctType":"linear","ctVal":"0.01",
           "ctValCcy":"BTC","settleCcy":"USDT","tickSz":"0.1","lotSz":"0.01","minSz":"0.01",
           "state":"live","listTime":"1573557408000"}]}
        """;

    private const string OkxFunding = """
        {"code":"0","msg":"","data":[
          {"instId":"BTC-USDT-SWAP","fundingRate":"0.0000356","fundingTime":"1789243200000",
           "nextFundingTime":"1789272000000"}]}
        """;

    private const string OkxMarkCandles = """
        {"code":"0","data":[["1789242180000","77038.7","77049.7","77037.7","77047.8","1"]],"msg":""}
        """;

    private const string OkxIndexCandles = """
        {"code":"0","data":[["1789242180000","77073.7","77080.9","77072.1","77079.2","1"]],"msg":""}
        """;

    private const string OkxOiHistory = """
        {"code":"0","data":[
          ["1789239600000","2752527.40000001374","27523.9951000001359","2121056862.79572047273939"]],"msg":""}
        """;

    [Fact]
    public async Task Okxs_index_series_is_addressed_by_the_pair_and_the_mark_series_by_the_instrument()
    {
        // THE trap on this venue. Both routes sit under /api/v5/market/ and both take a parameter
        // spelled instId — but the index route wants "BTC-USDT" and the mark route wants
        // "BTC-USDT-SWAP". Hand the swap name to the index route and it answers 200 with an empty
        // array, which reads exactly like a venue that publishes no index at all. That is how this
        // series came to be reported as absent the first time.
        var seen = new List<string>();
        var okx = new OkxPerpMarketData(new OkxClient(
            Recording(seen,
                ("/api/v5/public/instruments", OkxInstruments),
                ("/api/v5/public/funding-rate", OkxFunding),
                ("/api/v5/market/mark-price-candles", OkxMarkCandles),
                ("/api/v5/market/index-candles", OkxIndexCandles)),
            "https://okx.test"));

        // Discovery first: the pair is learned there, and without it the index route cannot be
        // addressed at all.
        await okx.GetInstrumentsAsync(CancellationToken.None);

        var mark = Assert.Single(await okx.GetPriceCandles1mAsync(
            "BTC-USDT-SWAP", "mark", From, To, CancellationToken.None));
        var index = Assert.Single(await okx.GetPriceCandles1mAsync(
            "BTC-USDT-SWAP", "index", From, To, CancellationToken.None));

        var markCall = Assert.Single(seen, u => u.Contains("mark-price-candles", StringComparison.Ordinal));
        var indexCall = Assert.Single(seen, u => u.Contains("index-candles", StringComparison.Ordinal));

        Assert.Contains("instId=BTC-USDT-SWAP", markCall, StringComparison.Ordinal);
        Assert.Contains("instId=BTC-USDT&", indexCall, StringComparison.Ordinal);
        Assert.DoesNotContain("instId=BTC-USDT-SWAP", indexCall, StringComparison.Ordinal);

        // Both land under the instrument regardless of how they were fetched.
        Assert.Equal("BTC-USDT-SWAP", mark.ExchangeSymbol);
        Assert.Equal("BTC-USDT-SWAP", index.ExchangeSymbol);
        Assert.Equal(77047.8, mark.Close, 6);
        Assert.Equal(77079.2, index.Close, 6);
    }

    [Fact]
    public async Task Okx_asks_for_an_index_series_it_has_no_pair_for_by_not_asking()
    {
        // Discovery has not run, so the pair behind this instrument is unknown. Empty, rather than
        // sending the swap name to a route that does not use it and storing whatever came back.
        var seen = new List<string>();
        var okx = new OkxPerpMarketData(new OkxClient(
            Recording(seen, ("/api/v5/market/index-candles", OkxIndexCandles)), "https://okx.test"));

        Assert.Empty(await okx.GetPriceCandles1mAsync(
            "BTC-USDT-SWAP", "index", From, To, CancellationToken.None));
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Okxs_open_interest_history_is_this_instruments_own_and_carries_the_venues_notional()
    {
        // rubik has TWO routes a word apart. open-interest-VOLUME is keyed by currency and sums
        // every contract on the coin; open-interest-HISTORY takes an instId. The first was found
        // first, and this adapter's first version concluded from it that the venue serves no
        // per-instrument series.
        //
        // The row is [ts, contracts, coins, usd]. Its own arithmetic proves which is which:
        // 2 752 527.4 contracts × 0.01 = 27 523.99 BTC, and that at ~77 000 is the 2.12 bn quoted.
        var seen = new List<string>();
        var okx = new OkxPerpMarketData(new OkxClient(
            Recording(seen, ("/api/v5/rubik/stat/contracts/open-interest-history", OkxOiHistory)),
            "https://okx.test"));

        var b = Assert.Single(await okx.GetOpenInterestHistoryAsync(
            "BTC-USDT-SWAP", From, To, CancellationToken.None));

        Assert.Contains("open-interest-history", Assert.Single(seen), StringComparison.Ordinal);
        Assert.Contains("instId=BTC-USDT-SWAP", seen[0], StringComparison.Ordinal);

        Assert.Equal(3600, b.IntervalSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_239_600_000), b.BucketTime);

        // CONTRACTS, the same unit the snapshot's open-interest column holds for this venue, so the
        // two are one measurement at two cadences. The coin column would be a hundredth of it.
        Assert.Equal(2_752_527.4, b.Close, 1);

        // The venue's own notional, never our own oi × mark.
        Assert.Equal(2_121_056_862.8, b.Quote!.Value, 1);

        // One point per bucket; the venue does not aggregate it into an OHLC and none is invented.
        Assert.Null(b.Open);
        Assert.Null(b.High);
        Assert.Null(b.Low);
    }

    // ── MEXC ─────────────────────────────────────────────────────────────────────────────────

    private const string MexcFundingPage = """
        {"success":true,"code":0,"data":{"pageSize":100,"totalCount":1618,"totalPage":540,
         "currentPage":1,"resultList":[
          {"symbol":"BTC_USDT","fundingRate":0.000051,"settleTime":1789228800000,"collectCycle":8},
          {"symbol":"BTC_USDT","fundingRate":0.000043,"settleTime":1789200000000,"collectCycle":8},
          {"symbol":"BTC_USDT","fundingRate":0.00006,"settleTime":1789171200000,"collectCycle":8}]}}
        """;

    [Fact]
    public async Task Mexcs_funding_is_the_settled_series_and_no_longer_our_own_reading_of_the_current_rate()
    {
        // What stood here before was a hack with a comment defending it: the CURRENT rate, stamped
        // at the settlement it had not reached yet. That number is a forecast wearing a measurement's
        // timestamp, and it would have been overwritten by a different forecast on the next pass.
        // contract/funding_rate/history is real and 1 618 payments deep on this symbol.
        var seen = new List<string>();
        var mexc = new MexcPerpMarketData(new MexcClient(
            Recording(seen, ("/api/v1/contract/funding_rate/history", MexcFundingPage)),
            "https://contract.test"));

        var rows = await mexc.GetFundingHistoryAsync(
            "BTC_USDT",
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_100_000_000),
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_300_000_000),
            CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0.000051, rows[0].Rate, 9);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_228_800_000), rows[0].FundingTime);

        // Eight hours apart, which is this contract's own collectCycle — three settlements, not one
        // rate written three times.
        var instants = rows.Select(r => r.FundingTime).OrderBy(x => x).ToArray();
        Assert.Equal(TimeSpan.FromHours(8), instants[1] - instants[0]);
        Assert.Equal(TimeSpan.FromHours(8), instants[2] - instants[1]);
    }

    [Fact]
    public async Task Mexc_stops_walking_pages_when_the_venue_hands_back_a_short_one()
    {
        // The stub answers every page with the same three rows. A walk that kept going because
        // totalPage says 540 would issue twenty requests per symbol against a venue whose
        // reconnaissance specifically refused to be pushed — the short page is the stop signal,
        // ahead of the page count the venue advertises.
        var seen = new List<string>();
        var mexc = new MexcPerpMarketData(new MexcClient(
            Recording(seen, ("/api/v1/contract/funding_rate/history", MexcFundingPage)),
            "https://contract.test"));

        await mexc.GetFundingHistoryAsync("BTC_USDT", From, To, CancellationToken.None);

        Assert.Single(seen);
        Assert.Contains("page_num=1", seen[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mexc_keeps_settlements_outside_the_window_it_was_asked_for()
    {
        var mexc = new MexcPerpMarketData(new MexcClient(
            Stub(("/api/v1/contract/funding_rate/history", MexcFundingPage)), "https://contract.test"));

        // Ends between the second and the newest settlement.
        var rows = await mexc.GetFundingHistoryAsync(
            "BTC_USDT",
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_100_000_000),
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_210_000_000),
            CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.FundingTime == DateTimeOffset.FromUnixTimeMilliseconds(1_789_228_800_000));
    }

    // ── stubs ────────────────────────────────────────────────────────────────────────────────

    private static HttpClient Stub(params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, null));

    /// <summary>The same stub, appending every request's full URI to <paramref name="seen"/> — the
    /// only way to tell these series apart, since several are one route with a different
    /// parameter.</summary>
    private static HttpClient Recording(List<string> seen, params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, seen));

    private sealed class RouteStub((string PathEndsWith, string Body)[] routes, List<string>? seen)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            seen?.Add(uri.ToString());

            var body = routes.LastOrDefault(
                r => uri.AbsolutePath.EndsWith(r.PathEndsWith, StringComparison.Ordinal)).Body;

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }
}

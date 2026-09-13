using System.Net;
using System.Text;
using CryptoSmithX.MarketData.Connectors.Bybit;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Bybit spot — row 12 of plans/exchange-roadmap.md. Fixtures are trimmed copies of live responses
/// taken 2026-09-13.
///
/// <b>One client, two categories, and three shapes that differ.</b> Every v5 market route takes
/// <c>category</c>, which makes it tempting to treat spot as linear with a different word in the
/// query string. It is not, and none of the differences announces itself: the lot filter uses other
/// names for the same two ideas, the ticker offers a USD valuation where a perpetual offers an
/// index, and two routes do not exist at all. Each of those would produce a column that is empty or
/// plausible rather than an error.
/// </summary>
public sealed class BybitSpotTests
{
    private static HttpClient Stub(params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, null));

    private static HttpClient Recording(List<string> seen, params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, seen));

    /// <summary>A live row. Note lotSizeFilter: basePrecision and minOrderAmt, and no qtyStep or
    /// minNotionalValue anywhere in it.</summary>
    private const string Instruments = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          {"symbolId":9,"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","status":"Trading",
           "lotSizeFilter":{"basePrecision":"0.000001","quotePrecision":"0.0000001",
                            "minOrderQty":"0.000001","maxOrderQty":"230","minOrderAmt":"5",
                            "maxOrderAmt":"8000000"},
           "priceFilter":{"tickSize":"0.1"}}],"nextPageCursor":""}}
        """;

    /// <summary>A live row: no markPrice, no indexPrice, no fundingRate, no openInterest — and a
    /// usdIndexPrice, which is none of those.</summary>
    private const string Tickers = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          {"symbol":"BTCUSDT","bid1Price":"76733.8","bid1Size":"0.494301","ask1Price":"76733.9",
           "ask1Size":"0.364319","lastPrice":"76733.3","prevPrice24h":"77368.8",
           "price24hPcnt":"-0.0082","highPrice24h":"77511.8","lowPrice24h":"76594.1",
           "turnover24h":"153564492.50389082","volume24h":"1988.890982",
           "usdIndexPrice":"76702.005194"}]}}
        """;

    private const string Book = """
        {"retCode":0,"retMsg":"OK","result":{"s":"BTCUSDT","ts":1789291624000,
         "a":[["76733.9","0.364319"],["77400.0","1.5"]],
         "b":[["76733.8","0.197869"],["76100.0","2.0"]]}}
        """;

    private const string Trades = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          {"execId":"2290000001207528689","symbol":"BTCUSDT","price":"76733.3","size":"0.00152",
           "side":"Buy","time":"1789291624322"},
          {"execId":"2290000001207528688","symbol":"BTCUSDT","price":"76733.1","size":"0.002903",
           "side":"Sell","time":"1789291624100"}]}}
        """;

    private const string Kline = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          ["1789291620000","76726.4","76733.3","76726.4","76733.3","0.331348","25424.0447035"]]}}
        """;

    private static BybitSpotMarketData Spot(params (string, string)[] extra) =>
        new(new BybitClient(
            Stub([("/v5/market/instruments-info", Instruments), ("/v5/market/tickers", Tickers), .. extra]),
            "https://bybit.test",
            BybitClient.Spot));

    [Fact]
    public async Task The_quantity_step_comes_from_basePrecision_because_spot_has_no_qtyStep()
    {
        // The linear name is absent from every one of the 538 spot rows. Read it here and the step
        // is null on the whole venue — no error, no warning, just a column that never fills.
        var i = Assert.Single(await Spot().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal(0.000001m, i.QtyStep);
        Assert.Equal(0.000001m, i.MinQty);

        // minOrderAmt is minNotionalValue by another name.
        Assert.Equal(5m, i.MinNotional);
        Assert.Equal(0.1m, i.PriceStep);
    }

    [Fact]
    public async Task A_spot_pair_is_not_a_contract_and_pays_no_funding()
    {
        var i = Assert.Single(await Spot().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal(1m, i.ContractMultiplier);
        Assert.Null(i.FundingIntervalHours);

        // instruments-info carries no launch time on this category; linear has one.
        Assert.Null(i.ListedAt);
        Assert.Equal(InstrumentStatus.Trading, i.Status);
    }

    [Fact]
    public async Task Every_route_carries_the_spot_category_and_not_the_default()
    {
        // The client defaults to linear so the perpetual adapter reads unchanged. A spot adapter
        // built without the category would silently collect the futures market into a spot segment
        // — 200s all the way down, and every row wrong.
        var seen = new List<string>();
        var spot = new BybitSpotMarketData(new BybitClient(
            Recording(seen,
                ("/v5/market/instruments-info", Instruments),
                ("/v5/market/tickers", Tickers),
                ("/v5/market/orderbook", Book),
                ("/v5/market/recent-trade", Trades)),
            "https://bybit.test",
            BybitClient.Spot));

        await spot.GetInstrumentsAsync(CancellationToken.None);
        await spot.GetTickersAsync(CancellationToken.None);
        await spot.GetOrderBookAsync("BTCUSDT", CancellationToken.None);

        Assert.NotEmpty(seen);
        Assert.All(seen, u => Assert.Contains("category=spot", u, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_usd_valuation_is_not_stored_as_an_index_price()
    {
        // usdIndexPrice is the venue's USD valuation of the BASE COIN for margin purposes. A
        // perpetual's index tracks the PAIR. On BTCUSDC the two sit within a stablecoin's drift of
        // each other, so storing one under the other's name would read as correct and be a different
        // measurement.
        var t = Assert.Single(await Spot().GetTickersAsync(CancellationToken.None));

        Assert.Null(t.IndexPrice);
        Assert.Null(t.MarkPrice);
        Assert.Null(t.FundingRate);
        Assert.Null(t.OpenInterest);

        Assert.Equal(76_733.3, t.LastPrice!.Value, 6);
        Assert.Equal(153_564_492.50389082, t.Turnover24h!.Value, 4);
        Assert.Equal(1_988.890982, t.Volume24hBase!.Value, 6);
    }

    [Fact]
    public void Spot_declares_no_dataset_a_spot_market_cannot_have()
    {
        var caps = Spot().Capabilities.Select(c => c.DatasetCode).ToArray();

        Assert.Equal(
            ["candles", "depth", "discovery", "snapshot", "spec_versions", "trades"],
            caps.Order());

        foreach (var absent in new[]
                 { "funding", "open_interest", "liquidations", "candles_mark", "candles_index", "book" })
        {
            Assert.DoesNotContain(absent, caps);
        }
    }

    [Fact]
    public async Task The_funding_route_is_never_called_because_it_does_not_exist_for_this_category()
    {
        // Not "returns empty": not asked. The stub 404s everything it was not given, so a call here
        // would throw rather than pass quietly.
        var seen = new List<string>();
        var spot = new BybitSpotMarketData(new BybitClient(
            Recording(seen, ("/v5/market/tickers", Tickers)), "https://bybit.test", BybitClient.Spot));

        Assert.Empty(await spot.GetFundingHistoryAsync(
            "BTCUSDT", DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Empty(seen);
    }

    [Fact]
    public async Task The_side_is_the_takers_own_and_needs_no_inverting()
    {
        // Bybit states the aggressor directly where Binance encodes the resting party in a maker
        // flag. Two venues, two conventions, and the same column.
        var spot = Spot(("/v5/market/orderbook", Book), ("/v5/market/recent-trade", Trades));
        await spot.GetOrderBookAsync("BTCUSDT", CancellationToken.None);

        var trades = spot.DrainTrades().OrderBy(t => t.EventTime).ToList();
        Assert.Equal(2, trades.Count);
        Assert.Equal("sell", trades[0].TakerSide);
        Assert.Equal("buy", trades[1].TakerSide);
    }

    [Fact]
    public async Task The_book_is_base_units_and_stamped_with_the_venues_own_clock()
    {
        var depth = await Spot(("/v5/market/orderbook", Book), ("/v5/market/recent-trade", Trades))
            .GetOrderBookAsync("BTCUSDT", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_291_624_000), depth.At);

        // price x size with no multiplier between them.
        Assert.Equal(76_733.8 * 0.197869, depth.Bid50Bps!.Value, 4);
    }

    [Fact]
    public async Task A_spot_bar_has_seven_fields_and_no_count_of_prints()
    {
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1_789_291_800_000);
        var c = Assert.Single(await Spot(("/v5/market/kline", Kline))
            .GetCandles1mAsync("BTCUSDT", to - TimeSpan.FromHours(1), to, CancellationToken.None));

        Assert.Equal(76_726.4, c.Open, 6);
        Assert.Equal(0.331348, c.Volume, 6);
        Assert.Equal(25_424.0447035, c.VolumeQuote!.Value, 6);

        // Neither surface reports one, so the column stays empty rather than holding a zero that
        // would read as "nothing traded".
        Assert.Null(c.TradeCount);
    }

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

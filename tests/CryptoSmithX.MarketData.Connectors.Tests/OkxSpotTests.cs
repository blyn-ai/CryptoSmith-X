using System.Net;
using System.Text;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Okx;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// OKX spot — row 13 of plans/exchange-roadmap.md. Fixtures are trimmed copies of live responses
/// taken 2026-09-13.
///
/// <b>The trap on this venue is a field that keeps its name and changes its meaning.</b> On a swap
/// <c>volCcy24h</c> is the base volume; on spot it is the quote volume, which is the turnover
/// itself. Both are plausible numbers in a column that ranks venues against each other, and the
/// wrong reading is out by the price.
/// </summary>
public sealed class OkxSpotTests
{
    private static HttpClient Stub(params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, null));

    private static HttpClient Recording(List<string> seen, params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, seen));

    /// <summary>A live row, trimmed. Note baseCcy/quoteCcy, and ctVal/ctMult empty.</summary>
    private const string Instruments = """
        {"code":"0","msg":"","data":[
          {"instType":"SPOT","instId":"BTC-USDT","baseCcy":"BTC","quoteCcy":"USDT",
           "tickSz":"0.1","lotSz":"0.00000001","minSz":"0.00001","state":"live",
           "listTime":"1611907686000","ctVal":"","ctMult":"","ctType":"","instFamily":""}]}
        """;

    /// <summary>A live row. vol24h is 2 045 BTC and volCcy24h is 157 946 208 USDT, which at a price
    /// of 76 624 is the same trade flow counted in the two currencies.</summary>
    private const string Tickers = """
        {"code":"0","msg":"","data":[
          {"instType":"SPOT","instId":"BTC-USDT","last":"76624","lastSz":"0.00001501",
           "askPx":"76626.6","askSz":"0.0109","bidPx":"76626.5","bidSz":"0.48177826",
           "open24h":"77378.8","high24h":"77507.1","low24h":"76600",
           "volCcy24h":"157946208.103004577","vol24h":"2045.4695125","ts":"1789291902975"}]}
        """;

    private const string Book = """
        {"code":"0","msg":"","data":[{"ts":"1789291902975",
         "asks":[["76626.6","0.05125738","1"],["77300.0","1.5","4"]],
         "bids":[["76626.5","0.48177826","13"],["76000.0","2.0","7"]]}]}
        """;

    private const string Trades = """
        {"code":"0","msg":"","data":[
          {"instId":"BTC-USDT","tradeId":"1056455196","px":"76607.4","sz":"0.02316327",
           "side":"sell","ts":"1789291919512"},
          {"instId":"BTC-USDT","tradeId":"1056455195","px":"76607.5","sz":"0.00078344",
           "side":"buy","ts":"1789291918809"}]}
        """;

    private const string Candles = """
        {"code":"0","msg":"","data":[
          ["1789291920000","76607.5","76609.5","76607.5","76609.5","0.0229368","1757.130928","1757.130928","0"]]}
        """;

    private static OkxSpotMarketData Spot(params (string, string)[] extra) =>
        new(new OkxClient(
            Stub([("/api/v5/public/instruments", Instruments), ("/api/v5/market/tickers", Tickers), .. extra]),
            "https://okx.test",
            OkxClient.Spot));

    [Fact]
    public async Task Turnover_is_published_on_spot_and_is_not_the_swaps_arithmetic()
    {
        // On the swap surface volCcy24h is the BASE volume and the turnover has to be worked out as
        // base x price. Carrying that over here multiplies by the price a second time: 157 946 208
        // would become 1.2e13, a number four orders of magnitude out, in the column the grid uses to
        // rank venues against each other.
        var t = Assert.Single(await Spot().GetTickersAsync(CancellationToken.None));

        Assert.Equal(157_946_208.103004577, t.Turnover24h!.Value, 3);
        Assert.Equal(2_045.4695125, t.Volume24hBase!.Value, 6);

        // And the two are the same flow counted in the two currencies: dividing one by the other
        // gives an average price, and it has to fall inside the day's own range — 76 600 to 77 507
        // on this fixture. Were volCcy24h the base volume, as it is on a swap, the ratio would come
        // out at one.
        var implied = t.Turnover24h!.Value / t.Volume24hBase!.Value;
        Assert.InRange(implied, 76_600, 77_507);
    }

    [Fact]
    public async Task Spot_names_its_two_sides_where_a_swap_has_to_be_read_through_its_family()
    {
        var i = Assert.Single(await Spot().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal("BTC", i.BaseAssetRaw);
        Assert.Equal("USDT", i.QuoteAssetRaw);

        // ctVal and ctMult are empty strings on every spot row. There is no contract to size.
        Assert.Equal(1m, i.ContractMultiplier);
        Assert.Null(i.FundingIntervalHours);

        Assert.Equal(0.1m, i.PriceStep);
        Assert.Equal(0.00000001m, i.QtyStep);
        Assert.Equal(0.00001m, i.MinQty);

        // This route states no minimum order value on spot. Null is "the venue does not define one".
        Assert.Null(i.MinNotional);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_611_907_686_000), i.ListedAt);
    }

    [Fact]
    public async Task Every_route_carries_the_spot_type_and_not_the_default()
    {
        // The client defaults to SWAP so the perpetual adapter reads unchanged. Built without the
        // type, this adapter would collect the swap market into a spot segment — 200s all the way
        // down, every row wrong, and the instrument ids similar enough to pass a glance.
        var seen = new List<string>();
        var spot = new OkxSpotMarketData(new OkxClient(
            Recording(seen,
                ("/api/v5/public/instruments", Instruments),
                ("/api/v5/market/tickers", Tickers)),
            "https://okx.test",
            OkxClient.Spot));

        await spot.GetInstrumentsAsync(CancellationToken.None);
        await spot.GetTickersAsync(CancellationToken.None);

        Assert.Equal(2, seen.Count);
        Assert.All(seen, u => Assert.Contains("instType=SPOT", u, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_four_perpetual_routes_are_never_called()
    {
        // mark-price, open-interest, funding-rate and liquidation-orders do not exist for
        // instType=SPOT. The stub 404s anything it was not given, so a call would throw rather than
        // pass quietly.
        var seen = new List<string>();
        var spot = new OkxSpotMarketData(new OkxClient(
            Recording(seen,
                ("/api/v5/public/instruments", Instruments),
                ("/api/v5/market/tickers", Tickers)),
            "https://okx.test",
            OkxClient.Spot));

        await spot.GetInstrumentsAsync(CancellationToken.None);
        var t = Assert.Single(await spot.GetTickersAsync(CancellationToken.None));

        Assert.Empty(await spot.GetFundingHistoryAsync(
            "BTC-USDT", DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.DoesNotContain(seen, u => u.Contains("mark-price", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, u => u.Contains("open-interest", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, u => u.Contains("funding-rate", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, u => u.Contains("liquidation", StringComparison.Ordinal));

        Assert.Null(t.MarkPrice);
        Assert.Null(t.IndexPrice);
        Assert.Null(t.FundingRate);
        Assert.Null(t.OpenInterest);
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
    public async Task The_spot_ladder_is_base_units_and_needs_no_contract_size()
    {
        // The swap surface's ladder is in CONTRACTS, and a band computed without the multiplier is
        // out by a hundred on this venue. Spot has no contract at all, so the levels go through as
        // they arrive — stated rather than assumed, because the two adapters sit side by side.
        var depth = await Spot(("/api/v5/market/books-full", Book), ("/api/v5/market/trades", Trades))
            .GetOrderBookAsync("BTC-USDT", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_291_902_975), depth.At);
        Assert.Equal(76_626.5 * 0.48177826, depth.Bid50Bps!.Value, 4);
    }

    [Fact]
    public async Task The_side_is_the_takers_own_in_lower_case()
    {
        using var clock = FixtureClock.Freeze();
        var spot = Spot(("/api/v5/market/books-full", Book), ("/api/v5/market/trades", Trades));
        await spot.GetOrderBookAsync("BTC-USDT", CancellationToken.None);

        var trades = spot.DrainTrades().OrderBy(t => t.EventTime).ToList();
        Assert.Equal(2, trades.Count);
        Assert.Equal("buy", trades[0].TakerSide);
        Assert.Equal("sell", trades[1].TakerSide);
    }

    [Fact]
    public async Task The_quote_volume_of_a_bar_is_read_from_the_column_that_means_it_on_both_surfaces()
    {
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1_789_292_100_000);
        var c = Assert.Single(await Spot(("/api/v5/market/history-candles", Candles))
            .GetCandles1mAsync("BTC-USDT", to - TimeSpan.FromHours(1), to, CancellationToken.None));

        Assert.Equal(76_607.5, c.Open, 6);
        Assert.Equal(0.0229368, c.Volume, 9);
        Assert.Equal(1_757.130928, c.VolumeQuote!.Value, 6);
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

using System.Net;
using System.Text;
using CryptoSmithX.MarketData.Connectors.Binance;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Binance spot — row 11 of plans/exchange-roadmap.md. Fixtures are trimmed copies of live
/// responses taken 2026-09-13.
///
/// <b>Spot is where the assumptions written for perpetuals stop holding</b>, and each of them is a
/// place a wrong number would look perfectly reasonable: a market with no mark price, an order book
/// with no timestamp, a tape whose side field names the RESTING party, and a status vocabulary of
/// two values on a venue whose futures side has more.
/// </summary>
public sealed class BinanceSpotTests
{
    private static HttpClient Stub(params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, null));

    private static HttpClient Recording(List<string> seen, params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes, seen));

    private const string ExchangeInfo = """
        {"symbols":[
          {"symbol":"BTCUSDT","status":"TRADING","baseAsset":"BTC","quoteAsset":"USDT",
           "isSpotTradingAllowed":true,
           "filters":[{"filterType":"PRICE_FILTER","minPrice":"0.01000000","tickSize":"0.01000000"},
                      {"filterType":"LOT_SIZE","minQty":"0.00001000","stepSize":"0.00001000"},
                      {"filterType":"NOTIONAL","minNotional":"5.00000000"}]},
          {"symbol":"HALTUSDT","status":"BREAK","baseAsset":"HALT","quoteAsset":"USDT",
           "isSpotTradingAllowed":true,
           "filters":[{"filterType":"PRICE_FILTER","tickSize":"0.00010000"},
                      {"filterType":"LOT_SIZE","minQty":"1.00000000","stepSize":"1.00000000"},
                      {"filterType":"NOTIONAL","minNotional":"5.00000000"}]},
          {"symbol":"MARGINUSDT","status":"BREAK","baseAsset":"MARGIN","quoteAsset":"USDT",
           "isSpotTradingAllowed":false,
           "filters":[{"filterType":"PRICE_FILTER","tickSize":"0.00010000"},
                      {"filterType":"LOT_SIZE","minQty":"1.00000000","stepSize":"1.00000000"}]}]}
        """;

    private const string Tickers = """
        [{"symbol":"BTCUSDT","lastPrice":"77093.98000000","bidPrice":"77093.98000000",
          "bidQty":"0.00514000","askPrice":"77093.99000000","askQty":"5.82558000",
          "volume":"7156.14008000","quoteVolume":"552945047.30185840",
          "openTime":1789200641010,"closeTime":1789287041010,"count":713160}]
        """;

    private const string Depth = """
        {"lastUpdateId":100052074652,
         "bids":[["77093.98000000","0.00514000"],["76500.00000000","2.00000000"]],
         "asks":[["77093.99000000","5.82558000"],["77700.00000000","3.00000000"]]}
        """;

    private const string AggTrades = """
        [{"a":4062562944,"p":"77093.98000000","q":"0.00037000","f":6677664898,"l":6677664898,
          "T":1789287040309,"m":true,"M":true},
         {"a":4062562945,"p":"77093.99000000","q":"0.00058000","f":6677664899,"l":6677664900,
          "T":1789287042941,"m":false,"M":true}]
        """;

    private const string Klines = """
        [[1789287000000,"77120.01000000","77130.00000000","77093.98000000","77093.98000000",
          "2.29655000",1789287059999,"177072.06031380",1090,"0.07953000","6131.94332880","0"]]
        """;

    private static BinanceSpotMarketData Spot(params (string, string)[] extra) =>
        new(new BinanceSpotClient(Stub([
            ("/api/v3/exchangeInfo", ExchangeInfo),
            ("/api/v3/ticker/24hr", Tickers),
            .. extra]), "https://spot.test"));

    // ── discovery ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pair_the_venue_says_cannot_be_traded_on_spot_stays_out_of_a_spot_segment()
    {
        // Forty of the 1 086 USD-family rows carry isSpotTradingAllowed: false. Every one of them is
        // also BREAK today, which is exactly why the flag has to be read rather than inferred from
        // the status — the day those two stop agreeing, the status is not the one that answers the
        // question this segment is asking.
        var instruments = await Spot().GetInstrumentsAsync(CancellationToken.None);

        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "MARGINUSDT");
        Assert.Equal(["BTCUSDT", "HALTUSDT"], instruments.Select(i => i.ExchangeSymbol).Order());
    }

    [Fact]
    public async Task Spot_has_no_contract_and_no_funding_and_says_so_with_nulls()
    {
        var i = (await Spot().GetInstrumentsAsync(CancellationToken.None))
            .Single(x => x.ExchangeSymbol == "BTCUSDT");

        // Quantities are base-asset units. Nothing here is a contract, so the multiplier is one and
        // not a number that happens to equal one.
        Assert.Equal(1m, i.ContractMultiplier);

        // A spot pair pays no funding, and exchangeInfo carries no onboard date on this surface —
        // futures has one, spot does not.
        Assert.Null(i.FundingIntervalHours);
        Assert.Null(i.ListedAt);

        Assert.Equal(0.01m, i.PriceStep);
        Assert.Equal(0.00001m, i.QtyStep);
        Assert.Equal(0.00001m, i.MinQty);
        Assert.Equal(5m, i.MinNotional);
    }

    [Fact]
    public async Task Break_is_halted_and_an_unfamiliar_status_stops_discovery_rather_than_guessing()
    {
        var halted = (await Spot().GetInstrumentsAsync(CancellationToken.None))
            .Single(x => x.ExchangeSymbol == "HALTUSDT");
        Assert.Equal(InstrumentStatus.Halted, halted.Status);

        // The alternative — leaving an unknown status out of this pass — is not a quiet no-op:
        // DiscoveryCollector writes 'delisted' over anything missing from enough consecutive passes,
        // so silence would become a lifecycle event the venue never announced.
        var strange = new BinanceSpotMarketData(new BinanceSpotClient(
            Stub(("/api/v3/exchangeInfo",
                """
                {"symbols":[{"symbol":"XUSDT","status":"PRE_TRADING","baseAsset":"X","quoteAsset":"USDT",
                  "isSpotTradingAllowed":true,
                  "filters":[{"filterType":"PRICE_FILTER","tickSize":"0.1"},
                             {"filterType":"LOT_SIZE","minQty":"1","stepSize":"1"}]}]}
                """)),
            "https://spot.test"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => strange.GetInstrumentsAsync(CancellationToken.None));
        Assert.Contains("PRE_TRADING", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_symbols_own_json_is_kept_rather_than_what_we_understood_of_it()
    {
        // The audit column holds what the venue said. Serialising the parsed shape back would record
        // the fields this adapter happens to read and silently drop the rest.
        var i = (await Spot().GetInstrumentsAsync(CancellationToken.None))
            .Single(x => x.ExchangeSymbol == "BTCUSDT");

        Assert.Contains("\"minPrice\"", i.RawJson, StringComparison.Ordinal);
    }

    // ── snapshot ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_bulk_call_closes_the_snapshot_and_bookTicker_is_never_asked_for()
    {
        // ticker/24hr carries the rolling-window figures AND the live top of book, so the second
        // route would buy a second clock over the same two numbers. Measured: 24hr is 1 850 KB at
        // weight 80 and bookTicker 416 KB at weight 4, but bookTicker carries no turnover — asking
        // for both costs more than asking for one.
        var seen = new List<string>();
        var spot = new BinanceSpotMarketData(new BinanceSpotClient(
            Recording(seen, ("/api/v3/ticker/24hr", Tickers), ("/api/v3/ticker/bookTicker", "[]")),
            "https://spot.test"));

        var t = Assert.Single(await spot.GetTickersAsync(CancellationToken.None));

        Assert.Single(seen);
        Assert.DoesNotContain("bookTicker", seen[0], StringComparison.Ordinal);

        Assert.Equal(77_093.98, t.BidPrice!.Value, 6);
        Assert.Equal(0.00514, t.BidSize!.Value, 9);
        Assert.Equal(77_093.99, t.AskPrice!.Value, 6);
        Assert.Equal(5.82558, t.AskSize!.Value, 9);
    }

    [Fact]
    public async Task The_seven_figures_a_spot_market_does_not_have_are_null_and_not_zero()
    {
        // Zero is indistinguishable from a measurement. A spot market has no mark, no index, no
        // funding and no open interest, and the column has to be able to say that rather than
        // report a flat one.
        var t = Assert.Single(await Spot().GetTickersAsync(CancellationToken.None));

        Assert.Null(t.MarkPrice);
        Assert.Null(t.IndexPrice);
        Assert.Null(t.FundingRate);
        Assert.Null(t.FundingRatePredicted);
        Assert.Null(t.NextFundingAt);
        Assert.Null(t.OpenInterest);
        Assert.Null(t.OpenInterestAt);

        // And the figures it does have are there.
        Assert.Equal(552_945_047.30185840, t.Turnover24h!.Value, 4);
        Assert.Equal(7_156.14008, t.Volume24hBase!.Value, 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_287_041_010), t.VenueTs);
    }

    [Fact]
    public void Spot_declares_exactly_what_a_spot_market_has()
    {
        var caps = Spot().Capabilities.Select(c => c.DatasetCode).ToArray();

        Assert.Equal(
            ["candles", "depth", "discovery", "snapshot", "spec_versions", "trades"],
            caps.Order());

        // Each absence is a fact about the market, not about how far the work got: there is no
        // funding, no open interest and no liquidation feed on a spot venue; mark and index are
        // prices a spot market does not compute; and 'book' would promise a maintained socket book
        // where there is no socket at all.
        foreach (var absent in new[]
                 { "funding", "open_interest", "liquidations", "candles_mark", "candles_index", "book" })
        {
            Assert.DoesNotContain(absent, caps);
        }
    }

    // ── depth and tape ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_book_is_stamped_with_our_clock_because_this_route_carries_none()
    {
        // Not a shortcut: /api/v3/depth has no timestamp in the body and none in a header. The
        // futures side stamps depth_at with the venue's own event clock; this surface cannot, and
        // the column being separate is what lets the two say different things.
        var before = DateTimeOffset.UtcNow;
        var depth = await Spot(("/api/v3/depth", Depth), ("/api/v3/aggTrades", AggTrades))
            .GetOrderBookAsync("BTCUSDT", CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(depth);
        Assert.InRange(depth.At, before, after);

        // Base-asset quantities, so the band is price x size with no multiplier in between.
        Assert.NotNull(depth.Bid50Bps);
        Assert.Equal(77_093.98 * 0.00514, depth.Bid50Bps!.Value, 4);
    }

    [Fact]
    public async Task The_maker_flag_names_the_resting_side_and_is_read_that_way()
    {
        using var clock = FixtureClock.Freeze();
        // 'm' is TRUE when the BUYER was the maker, which means the TAKER SOLD. Read as "was a buy",
        // every print on the venue changes sides — and nothing downstream could tell, because both
        // answers are a valid side.
        var spot = Spot(("/api/v3/depth", Depth), ("/api/v3/aggTrades", AggTrades));
        await spot.GetOrderBookAsync("BTCUSDT", CancellationToken.None);

        var trades = spot.DrainTrades().OrderBy(t => t.EventTime).ToList();
        Assert.Equal(2, trades.Count);

        Assert.Equal("sell", trades[0].TakerSide);
        Assert.Equal("buy", trades[1].TakerSide);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_287_040_309), trades[0].EventTime);
    }

    [Fact]
    public async Task The_last_trades_instant_comes_from_the_tape_because_the_ticker_has_none()
    {
        var spot = Spot(("/api/v3/depth", Depth), ("/api/v3/aggTrades", AggTrades));

        Assert.Null(Assert.Single(await spot.GetTickersAsync(CancellationToken.None)).LastTradeAt);

        await spot.GetOrderBookAsync("BTCUSDT", CancellationToken.None);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_287_042_941),
            Assert.Single(await spot.GetTickersAsync(CancellationToken.None)).LastTradeAt);
    }

    [Fact]
    public async Task A_bar_still_forming_is_never_returned()
    {
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1_789_287_030_000);
        var from = to - TimeSpan.FromHours(1);

        var spot = Spot(("/api/v3/klines", Klines));

        // The bar opens at 1789287000000 and closes a minute later, past `to`.
        Assert.Empty(await spot.GetCandles1mAsync("BTCUSDT", from, to, CancellationToken.None));

        var closed = await spot.GetCandles1mAsync(
            "BTCUSDT", from, to + TimeSpan.FromMinutes(2), CancellationToken.None);

        var c = Assert.Single(closed);
        Assert.Equal(77_120.01, c.Open, 6);
        Assert.Equal(2.29655, c.Volume, 6);
        Assert.Equal(177_072.06031380, c.VolumeQuote!.Value, 4);
        Assert.Equal(1090, c.TradeCount);
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

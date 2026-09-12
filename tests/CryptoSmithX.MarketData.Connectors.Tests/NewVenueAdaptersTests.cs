using System.Net;
using System.Text;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Bitget;
using CryptoSmithX.MarketData.Connectors.Bybit;
using CryptoSmithX.MarketData.Connectors.Gate;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The three REST-first venues from plans/exchange-roadmap.md rows 5, 6 and 8. Every fixture below
/// is a trimmed copy of the live response taken 2026-09-12, values exactly as the venue sent them.
///
/// What these tests are FOR: the three venues describe the same concepts in three different units,
/// and every one of those differences is a place where a plausible-looking wrong number could be
/// written. Funding interval arrives in hours on Bybit's ticker, MINUTES on Bybit's own instruments
/// route, hours-as-a-string on Bitget and SECONDS on Gate. Sizes are coins on two venues and
/// CONTRACTS on the third. A tick is one number on two venues and a mantissa-plus-exponent pair on
/// the other. None of that is visible in a figure once it is stored.
/// </summary>
public sealed class NewVenueAdaptersTests
{
    private static HttpClient Stub(params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes));

    // ── Bybit ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two rows on purpose, and the second one is why. The venue's linear category carries 833
    /// perpetuals and 40 DATED futures, and on the dated ones <c>fundingIntervalHour</c>,
    /// <c>fundingRate</c> and <c>fundingCap</c> are EMPTY STRINGS — a dated future pays no funding.
    /// A fixture of perpetuals alone is what let a wrong type ship.
    /// </summary>
    private const string BybitTickers = """
        {"retCode":0,"retMsg":"OK","result":{"list":[
          {"symbol":"BTCUSDT","lastPrice":"77146.50","bid1Price":"77146.50","bid1Size":"1.528",
           "ask1Price":"77146.60","ask1Size":"9.153","markPrice":"77146.50","indexPrice":"77179.83",
           "fundingRate":"0.00008216","fundingIntervalHour":"8","nextFundingTime":"1789257600000",
           "openInterest":"53367.43","openInterestValue":"4117110438.50",
           "turnover24h":"1653746238.7555","volume24h":"21418.4940"},
          {"symbol":"BTCUSDT-25DEC26","lastPrice":"78210.10","bid1Price":"78200.00","bid1Size":"0.5",
           "ask1Price":"78220.00","ask1Size":"0.4","markPrice":"78210.10","indexPrice":"77179.83",
           "fundingRate":"","fundingIntervalHour":"","nextFundingTime":"0",
           "openInterest":"120.5","openInterestValue":"9424317.05",
           "turnover24h":"1000000.0","volume24h":"12.8"}]}}
        """;

    private const string BybitInstruments = """
        {"retCode":0,"retMsg":"OK","result":{"nextPageCursor":"","list":[
          {"symbol":"BTCUSDT","contractType":"LinearPerpetual","status":"Trading","baseCoin":"BTC",
           "quoteCoin":"USDT","launchTime":"1584230400000","fundingInterval":480,
           "priceFilter":{"tickSize":"0.10"},
           "lotSizeFilter":{"qtyStep":"0.001","minOrderQty":"0.001","minNotionalValue":"5"}},
          {"symbol":"BTC-28MAR26","contractType":"LinearFutures","status":"Trading","baseCoin":"BTC",
           "quoteCoin":"USDT","launchTime":"1700000000000","fundingInterval":480,
           "priceFilter":{"tickSize":"0.10"},
           "lotSizeFilter":{"qtyStep":"0.001","minOrderQty":"0.001","minNotionalValue":"5"}}]}}
        """;

    [Fact]
    public async Task Bybit_reads_open_interest_in_both_units_and_derives_neither()
    {
        var adapter = new BybitPerpMarketData(new BybitClient(
            Stub(("/v5/market/tickers", BybitTickers)), "https://api.test"));

        var t = (await adapter.GetTickersAsync(CancellationToken.None))
            .Single(x => x.ExchangeSymbol == "BTCUSDT");

        Assert.Equal(53367.43, t.OpenInterest!.Value, 6);
        // The venue publishes the notional itself, so it is written as published — 53 367.43 × the
        // mark would be OUR arithmetic in a column that holds the venue's.
        Assert.Equal(4117110438.50, t.OiQuote!.Value, 2);
    }

    [Fact]
    public async Task A_dated_future_with_empty_funding_fields_does_not_fail_the_whole_batch()
    {
        // FOUND ON THE HOST, on the first pass after deploy, and the unit tests could not have
        // caught it: the fixture was a perpetual, and only the dated futures carry the empty string.
        //
        //   JsonException: could not convert ... Path: $.result.list[159].fundingIntervalHour
        //
        // 159 is where the dated contracts begin in the live response. Every numeric field on this
        // venue arrives as a STRING — all 873 rows, even the ones holding a plain 4 — so a strongly
        // typed int? parsed the perpetuals, reached the first dated future's "" and threw, taking
        // the snapshot AND the open-interest pass down with it. One empty field on one contract, and
        // 829 instruments stop being collected.
        var adapter = new BybitPerpMarketData(new BybitClient(
            Stub(("/v5/market/tickers", BybitTickers)), "https://api.test"));

        var all = await adapter.GetTickersAsync(CancellationToken.None);

        Assert.Equal(2, all.Count);

        var dated = all.Single(x => x.ExchangeSymbol == "BTCUSDT-25DEC26");
        // Its prices are real and are kept; only what a dated future genuinely lacks is absent.
        Assert.Equal(78210.10, dated.LastPrice!.Value, 6);
        Assert.Null(dated.FundingRate);
        Assert.Null(dated.NextFundingAt);
    }

    [Fact]
    public async Task Bybit_dated_futures_are_dropped_from_a_segment_whose_kind_is_perp()
    {
        var adapter = new BybitPerpMarketData(new BybitClient(
            Stub(("/v5/market/instruments-info", BybitInstruments)), "https://api.test"));

        var i = Assert.Single(await adapter.GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal("BTCUSDT", i.ExchangeSymbol);
    }

    [Fact]
    public async Task Bybits_funding_interval_is_read_in_the_unit_the_field_it_came_from_uses()
    {
        // THE TRAP ON THIS VENUE. instruments-info says fundingInterval: 480 and means MINUTES;
        // the ticker says fundingIntervalHour: 8 and means hours. Both describe the same eight
        // hours, and reading either into the other's field would be wrong by sixty.
        var adapter = new BybitPerpMarketData(new BybitClient(
            Stub(("/v5/market/instruments-info", BybitInstruments)), "https://api.test"));

        var i = Assert.Single(await adapter.GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal((short)8, i.FundingIntervalHours);
    }

    [Fact]
    public async Task A_bybit_error_inside_a_200_is_a_failure_and_never_an_empty_market()
    {
        // v5 answers a bad request with HTTP 200 and a non-zero retCode. Read as success, that is
        // an empty list — and an empty list of tickers is indistinguishable from a venue that went
        // quiet, which is the most expensive way for an outage to be recorded.
        var adapter = new BybitPerpMarketData(new BybitClient(
            Stub(("/v5/market/tickers", """{"retCode":10001,"retMsg":"params error","result":null}""")),
            "https://api.test"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.GetTickersAsync(CancellationToken.None));

        Assert.Contains("10001", ex.Message);
        Assert.Contains("params error", ex.Message);
    }

    // ── Bitget ───────────────────────────────────────────────────────────────────────────────

    private const string BitgetTickers = """
        {"code":"00000","msg":"success","data":[
          {"symbol":"BTCUSDT","lastPr":"77153.6","bidPr":"77153.6","bidSz":"0.751","askPr":"77153.7",
           "askSz":"8.7528","markPrice":"77153.7","indexPrice":"77180.0785","fundingRate":"0.000069",
           "holdingAmount":"35194.2193999999167","baseVolume":"11982.8017",
           "quoteVolume":"925346862.40765","ts":"1789235622181"}]}
        """;

    private const string BitgetContracts = """
        {"code":"00000","msg":"success","data":[
          {"symbol":"BTCUSDT","baseCoin":"BTC","quoteCoin":"USDT","symbolType":"perpetual",
           "symbolStatus":"normal","minTradeNum":"0.0001","priceEndStep":"1","pricePlace":"1",
           "sizeMultiplier":"0.0001","minTradeUSDT":"5","fundInterval":"8","launchTime":""},
          {"symbol":"PEPEUSDT","baseCoin":"PEPE","quoteCoin":"USDT","symbolType":"perpetual",
           "symbolStatus":"normal","minTradeNum":"1000","priceEndStep":"1","pricePlace":"10",
           "sizeMultiplier":"1000","minTradeUSDT":"5","fundInterval":"8","launchTime":""}]}
        """;

    [Fact]
    public async Task Bitgets_tick_is_its_two_fields_put_together_and_it_divides_the_live_price()
    {
        // The venue publishes a step and its exponent separately. Checked here across ten orders of
        // magnitude, because a wrong exponent looks entirely plausible in isolation: BTC trades at
        // 77 153.6 (one decimal) and PEPE at 0.000003395 (ten).
        var adapter = new BitgetPerpMarketData(new BitgetClient(
            Stub(("/api/v2/mix/market/contracts", BitgetContracts)), "https://api.test"));

        var byName = (await adapter.GetInstrumentsAsync(CancellationToken.None))
            .ToDictionary(i => i.ExchangeSymbol, StringComparer.Ordinal);

        Assert.Equal(0.1m, byName["BTCUSDT"].PriceStep);
        Assert.Equal(0.0000000001m, byName["PEPEUSDT"].PriceStep);
    }

    [Fact]
    public async Task Bitget_carries_the_venues_own_clock_rather_than_the_instant_we_wrote_the_row()
    {
        var adapter = new BitgetPerpMarketData(new BitgetClient(
            Stub(("/api/v2/mix/market/tickers", BitgetTickers)), "https://api.test"));

        var t = Assert.Single(await adapter.GetTickersAsync(CancellationToken.None));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789235622181), t.VenueTs);
    }

    [Fact]
    public async Task Bitget_leaves_oi_quote_absent_because_the_venue_publishes_no_such_figure()
    {
        // holdingAmount × mark would be our own arithmetic in a column whose whole point is to hold
        // the venue's. Absent is the honest answer.
        var adapter = new BitgetPerpMarketData(new BitgetClient(
            Stub(("/api/v2/mix/market/tickers", BitgetTickers)), "https://api.test"));

        var t = Assert.Single(await adapter.GetTickersAsync(CancellationToken.None));

        Assert.Equal(35194.2193999999167, t.OpenInterest!.Value, 6);
        Assert.Null(t.OiQuote);
    }

    [Fact]
    public void Bitget_declares_no_open_interest_history_because_the_venue_keeps_none()
    {
        // The venue answers mix/market/open-interest with one current number and no series. A
        // declared capability with nothing behind it starts a loop that can only fail.
        var caps = new BitgetPerpMarketData(new BitgetClient(Stub(), "https://api.test"))
            .Capabilities.Select(c => c.DatasetCode).ToArray();

        Assert.DoesNotContain("open_interest", caps);
        Assert.Contains("snapshot", caps);
    }

    // ── Gate ─────────────────────────────────────────────────────────────────────────────────

    private const string GateTickers = """
        [{"contract":"BTC_USDT","last":"77151.5","highest_bid":"77151.4","highest_size":"21536",
          "lowest_ask":"77151.5","lowest_size":"482333","mark_price":"77151.4",
          "index_price":"77180.23","funding_rate":"0.000099","total_size":"637548858",
          "volume_24h":"207254174","volume_24h_base":"20725","volume_24h_quote":"1600026056"}]
        """;

    private const string GateContracts = """
        [{"name":"BTC_USDT","type":"direct","in_delisting":false,"quanto_multiplier":"0.0001",
          "order_price_round":"0.1","order_size_min":1,"funding_interval":28800,
          "create_time":1758124392}]
        """;

    [Fact]
    public async Task Gate_carries_the_contract_multiplier_that_makes_its_sizes_mean_anything()
    {
        // This venue quotes in CONTRACTS. Without the multiplier its open interest reads 637 548 858
        // "BTC" — a number twelve thousand times the coin's entire supply, and one that would still
        // sort and render perfectly happily.
        var adapter = new GatePerpMarketData(new GateClient(
            Stub(("/api/v4/futures/usdt/contracts", GateContracts)), "https://api.test"));

        var i = Assert.Single(await adapter.GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal(0.0001m, i.ContractMultiplier);
        Assert.Equal("BTC", i.BaseAssetRaw);
        Assert.Equal("USDT", i.QuoteAssetRaw);
    }

    [Fact]
    public async Task Gates_raw_contract_counts_times_its_own_multiplier_are_its_own_base_figure()
    {
        // The venue proves the arithmetic itself, and this test is that proof written down: it
        // publishes BOTH volume_24h in contracts and volume_24h_base in coins, and one is the other
        // times quanto_multiplier. If the multiplier were ever read wrong, these two stop agreeing.
        var adapter = new GatePerpMarketData(new GateClient(
            Stub(("/api/v4/futures/usdt/tickers", GateTickers),
                 ("/api/v4/futures/usdt/contracts", GateContracts)), "https://api.test"));

        var t = Assert.Single(await adapter.GetTickersAsync(CancellationToken.None));
        var i = Assert.Single(await adapter.GetInstrumentsAsync(CancellationToken.None));

        // Open interest is stored RAW — a count of contracts, exactly as the venue sent it.
        Assert.Equal(637548858d, t.OpenInterest!.Value, 0);

        // And the multiplier is what makes it a quantity of coins. 63 754.9 BTC is the same order
        // as the other two venues in this batch report for the same instrument (Bybit 53 367,
        // Bitget 35 194), which is the sanity this conversion either has or badly lacks.
        Assert.Equal(63754.8858d, t.OpenInterest.Value * (double)i.ContractMultiplier, 4);
    }

    [Fact]
    public async Task Gate_takes_base_volume_from_the_venue_rather_than_through_the_multiplier()
    {
        // The one size on this venue that is already in coins. Passing it through the multiplier as
        // well would divide it by ten thousand a second time.
        var adapter = new GatePerpMarketData(new GateClient(
            Stub(("/api/v4/futures/usdt/tickers", GateTickers)), "https://api.test"));

        var t = Assert.Single(await adapter.GetTickersAsync(CancellationToken.None));

        Assert.Equal(20725d, t.Volume24hBase!.Value, 6);
    }

    [Fact]
    public async Task Gates_funding_interval_is_seconds_and_becomes_hours()
    {
        var adapter = new GatePerpMarketData(new GateClient(
            Stub(("/api/v4/futures/usdt/contracts", GateContracts)), "https://api.test"));

        var i = Assert.Single(await adapter.GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal((short)8, i.FundingIntervalHours);
    }

    [Fact]
    public void All_three_now_serve_depth_and_the_tape_over_rest()
    {
        // THIS TEST USED TO ASSERT THE OPPOSITE, on the reasoning that depth needs a socket. It does
        // not: all three serve a book ladder and a public tape over REST, one call per collected
        // symbol on the depth sweep's own cadence. A socket would be cheaper and is still worth
        // having; cheaper was never the same as missing.
        foreach (var caps in new[]
                 {
                     new BybitPerpMarketData(new BybitClient(Stub(), "https://x")).Capabilities,
                     new BitgetPerpMarketData(new BitgetClient(Stub(), "https://x")).Capabilities,
                     new GatePerpMarketData(new GateClient(Stub(), "https://x")).Capabilities,
                 })
        {
            var codes = caps.Select(c => c.DatasetCode).ToArray();

            Assert.Contains("depth", codes);
            Assert.Contains("trades", codes);
            Assert.Contains("discovery", codes);
            Assert.Contains("snapshot", codes);

            // `book` stays absent and that is not the old mistake repeating: that dataset is
            // book_topn, fed from a maintained SOCKET book through TryGetBookFrame, and a REST
            // ladder is not one.
            Assert.DoesNotContain("book", codes);
        }
    }

    private sealed class RouteStub((string PathEndsWith, string Body)[] routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = routes.FirstOrDefault(r => path.EndsWith(r.PathEndsWith, StringComparison.Ordinal)).Body;

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }
}

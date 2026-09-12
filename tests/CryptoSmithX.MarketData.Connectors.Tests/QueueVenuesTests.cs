using System.Net;
using System.Text;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.CoinbaseIntx;
using CryptoSmithX.MarketData.Connectors.Mexc;
using CryptoSmithX.MarketData.Connectors.Okx;
using CryptoSmithX.MarketData.Connectors.Pacing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Rows 7, 9 and 10 of plans/exchange-roadmap.md — OKX, Coinbase International and MEXC. Fixtures
/// are trimmed copies of live responses taken 2026-09-12.
///
/// Between them these three add three MORE spellings of things the previous batch already spelled
/// three ways: funding interval is now also "the gap between two instants" (OKX) and NANOSECONDS
/// (INTX); sizes are contracts on two of the three; and MEXC sends JSON numbers where everyone else
/// sends strings. Each of those is a place a wrong number would look perfectly reasonable.
/// </summary>
public sealed class QueueVenuesTests
{
    private static HttpClient Stub(params (string PathEndsWith, string Body)[] routes) =>
        new(new RouteStub(routes));

    // ── OKX ──────────────────────────────────────────────────────────────────────────────────

    private const string OkxInstruments = """
        {"code":"0","msg":"","data":[
          {"instId":"BTC-USDT-SWAP","instFamily":"BTC-USDT","ctType":"linear","ctVal":"0.01",
           "ctValCcy":"BTC","settleCcy":"USDT","tickSz":"0.1","lotSz":"0.01","minSz":"0.01",
           "state":"live","listTime":"1573557408000"},
          {"instId":"BTC-USD-SWAP","instFamily":"BTC-USD","ctType":"inverse","ctVal":"100",
           "ctValCcy":"USD","settleCcy":"BTC","tickSz":"0.1","lotSz":"1","minSz":"1",
           "state":"live","listTime":"1573557408000"}]}
        """;

    private const string OkxTickers = """
        {"code":"0","msg":"","data":[
          {"instId":"BTC-USDT-SWAP","last":"77164.1","bidPx":"77164","bidSz":"413.28",
           "askPx":"77164.1","askSz":"994.78","vol24h":"2516624.7","volCcy24h":"25166.247",
           "ts":"1789238862168"}]}
        """;

    private const string OkxMark = """{"code":"0","data":[{"instId":"BTC-USDT-SWAP","markPx":"77164"}]}""";

    private const string OkxOi = """
        {"code":"0","data":[{"instId":"BTC-USDT-SWAP","oi":"2742898.93","oiCcy":"27428.9893",
          "oiUsd":"2116533273.24","ts":"1789238864106"}]}
        """;

    private const string OkxFunding = """
        {"code":"0","data":[{"instId":"BTC-USDT-SWAP","fundingRate":"0.0001",
          "fundingTime":"1789257600000","nextFundingTime":"1789286400000"}]}
        """;

    private const string OkxIndex = """{"code":"0","data":[{"instId":"BTC-USDT","idxPx":"77188.1"}]}""";

    private static OkxPerpMarketData Okx() => new(new OkxClient(Stub(
        ("/api/v5/public/instruments", OkxInstruments),
        ("/api/v5/market/tickers", OkxTickers),
        ("/api/v5/public/mark-price", OkxMark),
        ("/api/v5/public/open-interest", OkxOi),
        ("/api/v5/public/funding-rate", OkxFunding),
        ("/api/v5/market/index-tickers", OkxIndex)), "https://okx.test"));

    [Fact]
    public async Task Okx_carries_the_contract_size_that_makes_its_quantities_coins()
    {
        // The venue proves this itself in the ticker: vol24h 2 516 624.7 contracts × 0.01 is
        // 25 166.247, which is volCcy24h exactly. Read the multiplier wrong and open interest on
        // BTC reads 2.7 million coins — thirteen percent of everything that will ever exist.
        var i = Assert.Single(await Okx().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal("BTC-USDT-SWAP", i.ExchangeSymbol);
        Assert.Equal(0.01m, i.ContractMultiplier);
        Assert.Equal("BTC", i.BaseAssetRaw);
        Assert.Equal("USDT", i.QuoteAssetRaw);
    }

    [Fact]
    public async Task An_inverse_okx_swap_is_not_in_a_linear_segment()
    {
        // BTC-USD-SWAP is margined in the base asset and its contract is worth 100 USD, not a
        // fraction of a coin. Mixing it into this segment would put two different meanings of
        // "size" in one column.
        var all = await Okx().GetInstrumentsAsync(CancellationToken.None);

        Assert.DoesNotContain(all, x => x.ExchangeSymbol == "BTC-USD-SWAP");
    }

    [Fact]
    public async Task Okxs_funding_interval_is_the_gap_between_its_own_two_instants()
    {
        // A fifth spelling of the same concept. This venue states no interval anywhere — it states
        // when this period settles and when the next one does, and the difference is the answer.
        var i = Assert.Single(await Okx().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal((short)8, i.FundingIntervalHours);
    }

    [Fact]
    public async Task Okx_joins_the_index_through_the_pair_because_that_is_how_it_is_published()
    {
        // index-tickers is keyed "BTC-USDT" while the instrument is "BTC-USDT-SWAP". Joined on the
        // instrument id it would find nothing and the Index column would read empty on a venue that
        // publishes one.
        var t = Assert.Single(await Okx().GetTickersAsync(CancellationToken.None));

        Assert.Equal(77188.1, t.IndexPrice!.Value, 6);
        Assert.Equal(77164d, t.MarkPrice!.Value, 6);
    }

    [Fact]
    public async Task Okx_writes_the_quote_notional_the_venue_computed_and_not_its_own()
    {
        var t = Assert.Single(await Okx().GetTickersAsync(CancellationToken.None));

        Assert.Equal(2742898.93, t.OpenInterest!.Value, 6);
        Assert.Equal(2116533273.24, t.OiQuote!.Value, 2);
    }

    // ── Coinbase International ───────────────────────────────────────────────────────────────

    private const string IntxInstruments = """
        [{"symbol":"BTC-PERP","type":"PERP","base_asset_name":"BTC","quote_asset_name":"USDC",
          "base_increment":"0.0001","quote_increment":"0.1","min_quantity":"0.0001",
          "min_notional_value":"10","funding_interval":"3600000000000","trading_state":"TRADING",
          "open_interest":"1671.6435","qty_24hr":"15080.0668","notional_24hr":"1164672053.14",
          "quote":{"best_bid_price":"77170.9","best_bid_size":"4.4467","best_ask_price":"77171",
            "best_ask_size":"3.9628","trade_price":"77170.9","index_price":"77173.2",
            "mark_price":"77170.9","predicted_funding":"0.000012",
            "timestamp":"2026-09-12T18:47:45.092Z"}},
         {"symbol":"BTC-USDC","type":"SPOT","base_asset_name":"BTC","quote_asset_name":"USDC",
          "base_increment":"0.0001","quote_increment":"0.01","trading_state":"TRADING","quote":{}}]
        """;

    private static CoinbaseIntxPerpMarketData Intx(params (string, string)[] extra) =>
        new(new CoinbaseIntxClient(
            Stub([("/api/v1/instruments", IntxInstruments), .. extra]), "https://intx.test"));

    [Fact]
    public async Task Intx_keeps_its_spot_listings_out_of_a_perp_segment()
    {
        var i = Assert.Single(await Intx().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal("BTC-PERP", i.ExchangeSymbol);
    }

    [Fact]
    public async Task Intxs_funding_interval_is_nanoseconds_and_this_venue_really_funds_hourly()
    {
        // 3 600 000 000 000 ns. Read as milliseconds it would be forty-one days; as seconds, a
        // hundred and fourteen thousand years. Both would still be a number in a column.
        var i = Assert.Single(await Intx().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal((short)1, i.FundingIntervalHours);
    }

    [Fact]
    public async Task Intxs_predicted_funding_never_reaches_the_column_that_means_the_rate_in_force()
    {
        // The roadmap named this as the least certain thing it claimed about this venue, and it was
        // right: `predicted_funding` is a FORECAST. The realised series is a call per instrument.
        // Writing the forecast into funding_rate would put a guess where every other venue puts a
        // measurement, and the whole column would stop meaning one thing.
        var t = Assert.Single(await Intx().GetTickersAsync(CancellationToken.None));

        Assert.Null(t.FundingRate);
        Assert.Equal(0.000012, t.FundingRatePredicted!.Value, 9);
    }

    [Fact]
    public async Task Intxs_snapshot_carries_the_settled_rate_once_the_funding_pass_has_run()
    {
        // The forecast must never reach funding_rate, and it does not. But leaving that column empty
        // put a hole in this venue's row on every pair while the settled series sat in the database
        // beside it, collected hourly. There is no bulk route for the realised rate here, so the
        // adapter remembers what the funding pass already fetched rather than asking again.
        var intx = Intx(("/instruments/BTC-PERP/funding", IntxFunding));

        // Before the funding pass: empty, because nothing has been observed yet — not a guess from
        // the forecast standing beside it.
        var before = Assert.Single(await intx.GetTickersAsync(CancellationToken.None));
        Assert.Null(before.FundingRate);
        Assert.Equal(0.000012, before.FundingRatePredicted!.Value, 9);

        await intx.GetFundingHistoryAsync(
            "BTC-PERP",
            DateTimeOffset.Parse("2026-09-12T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-12T23:00:00Z"),
            CancellationToken.None);

        var after = Assert.Single(await intx.GetTickersAsync(CancellationToken.None));

        // The NEWEST settlement, not the oldest row on the page and not the forecast.
        Assert.Equal(0.000031, after.FundingRate!.Value, 9);
        Assert.Equal(0.000012, after.FundingRatePredicted!.Value, 9);
    }

    private const string IntxFunding = """
        {"pagination":{"result_limit":25,"result_offset":0},"results":[
          {"funding_rate":"0.000031","mark_price":"77143.3","event_time":"2026-09-12T21:00:00Z"},
          {"funding_rate":"0.000044","mark_price":"77112","event_time":"2026-09-12T20:00:00Z"}]}
        """;

    [Fact]
    public async Task Intx_reads_the_venues_own_clock_off_an_iso_instant()
    {
        var t = Assert.Single(await Intx().GetTickersAsync(CancellationToken.None));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 12, 18, 47, 45, 92, TimeSpan.Zero), t.VenueTs);
        Assert.Equal(4.4467, t.BidSize!.Value, 6);
    }

    // ── MEXC ─────────────────────────────────────────────────────────────────────────────────

    private const string MexcDetail = """
        {"success":true,"code":0,"data":[
          {"symbol":"BTC_USDT","baseCoin":"BTC","quoteCoin":"USDT","settleCoin":"USDT",
           "contractSize":0.0001,"priceUnit":0.1,"volUnit":1,"minVol":1,"state":0,
           "apiAllowed":true,"createTime":1591242684000},
          {"symbol":"DEAD_USDT","baseCoin":"DEAD","quoteCoin":"USDT","settleCoin":"USDT",
           "contractSize":1,"priceUnit":0.1,"volUnit":1,"minVol":1,"state":0,
           "apiAllowed":false,"createTime":1591242684000},
          {"symbol":"BTC_USD","baseCoin":"BTC","quoteCoin":"USD","settleCoin":"BTC",
           "contractSize":100,"priceUnit":0.1,"volUnit":1,"minVol":1,"state":0,
           "apiAllowed":true,"createTime":1591242684000}]}
        """;

    private const string MexcTicker = """
        {"success":true,"code":0,"data":[
          {"symbol":"BTC_USDT","lastPrice":77150.1,"bid1":77150.1,"ask1":77150.2,
           "indexPrice":77189.5,"fairPrice":77150.1,"fundingRate":6.3e-05,
           "holdVol":448807331,"volume24":245092560,"amount24":1893011514.74,
           "timestamp":1789238864827}]}
        """;

    private const string MexcFunding = """
        {"success":true,"code":0,"data":[
          {"symbol":"BTC_USDT","fundingRate":6.3e-05,"collectCycle":8,
           "nextSettleTime":1789257600000}]}
        """;

    private static MexcPerpMarketData Mexc(params (string, string)[] extra) =>
        new(new MexcClient(Stub([
            ("/api/v1/contract/detail", MexcDetail),
            ("/api/v1/contract/ticker", MexcTicker),
            ("/api/v1/contract/funding_rate", MexcFunding),
            .. extra]), "https://mexc.test"));

    [Fact]
    public async Task Mexc_drops_a_contract_its_own_api_will_not_serve()
    {
        // 44 of 1 192 contracts say apiAllowed: false. Kept in, every per-symbol collector would
        // rediscover that one failure at a time, forever.
        var i = Assert.Single(await Mexc().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal("BTC_USDT", i.ExchangeSymbol);
        Assert.Equal(0.0001m, i.ContractMultiplier);
    }

    [Fact]
    public async Task Mexc_keeps_its_inverse_contracts_out_of_a_linear_segment()
    {
        // BTC_USD settles in BTC: a contract there is worth 100 of the QUOTE, not 100 of the base.
        // Read as base units — which is what every other contract on this venue means — one contract
        // becomes a hundred BITCOIN, and the grid showed 2.8 trillion dollars of depth at 50 bps on
        // a venue whose whole book is a few million. A number that wrong is worse than an empty
        // column, because it reads as a measurement.
        //
        // Ten of the 1 192 are like this, all quoted in USD, and settleCoin equalling baseCoin is
        // the only thing on the route that says so.
        var instruments = await Mexc().GetInstrumentsAsync(CancellationToken.None);

        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "BTC_USD");
        Assert.Equal("BTC_USDT", Assert.Single(instruments).ExchangeSymbol);
    }

    [Fact]
    public async Task Mexcs_funding_interval_comes_off_the_funding_route_because_detail_has_none()
    {
        // contract/detail carries a fundingInterval field that is null on all 1 192 contracts. The
        // only place this venue states its own cycle is beside the rate.
        var i = Assert.Single(await Mexc().GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal((short)8, i.FundingIntervalHours);
    }

    [Fact]
    public async Task Mexc_has_no_size_beside_either_side_and_says_so()
    {
        // bid1 and ask1 are prices. Borrowing volume24 or holdVol into a size column would put a
        // day's flow where a resting quantity belongs.
        var t = Assert.Single(await Mexc().GetTickersAsync(CancellationToken.None));

        Assert.Equal(77150.1, t.BidPrice!.Value, 6);
        Assert.Null(t.BidSize);
        Assert.Null(t.AskSize);
        Assert.Equal(448807331d, t.OpenInterest!.Value, 0);
    }

    [Fact]
    public async Task A_mexc_510_under_a_200_becomes_a_real_rate_limit_and_not_a_success()
    {
        // THE WHOLE REASON THIS VENUE'S CLIENT IS DIFFERENT. Measured: at the venue's own
        // documented 10/s, half of 120 requests came back exactly like this — HTTP 200, body
        // {"success":false,"code":510}. Read as a success it is an empty market; read as anything
        // other than a 429 it never reaches VenueGate.Penalize, and this venue could never slow us
        // down at all.
        var adapter = Mexc(("/api/v1/contract/ticker",
            """{"success":false,"code":510,"message":"Requests are too frequent, please try again later"}"""));

        var ex = await Assert.ThrowsAsync<VenueRateLimitedException>(
            () => adapter.GetTickersAsync(CancellationToken.None));

        // A 429 to everything downstream, which is what makes the existing pacing work unchanged.
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.NotNull(ex.RetryAfter);
        Assert.True(ex.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Okxs_turnover_is_derived_because_this_route_publishes_no_quote_figure()
    {
        // vol24h is contracts and volCcy24h is the same volume in BASE units. There is no quote
        // column on this route, alone among the venues here — so the turnover cell was empty for the
        // exchange with the largest perpetual book on the grid.
        //
        // Base volume valued at the last traded price. Both terms come from this frame, and it is
        // the same arithmetic OKX itself does for oiUsd, which is taken as published.
        var t = (await Okx().GetTickersAsync(CancellationToken.None))
            .Single(x => x.ExchangeSymbol == "BTC-USDT-SWAP");

        Assert.Equal(25_166.247 * 77_164.1, t.Turnover24h!.Value, 0);

        // And the base volume still goes to its own column rather than being consumed by the
        // derivation.
        Assert.Equal(25_166.247, t.Volume24hBase!.Value, 6);
    }

    [Fact]
    public void Each_of_the_three_declares_exactly_what_its_own_venue_serves()
    {
        // Not a shared list, because these three genuinely differ — and the differences are facts
        // about the venues rather than about how far the work got.
        var okx = Okx().Capabilities.Select(c => c.DatasetCode).ToArray();
        var intx = Intx().Capabilities.Select(c => c.DatasetCode).ToArray();
        var mexc = Mexc().Capabilities.Select(c => c.DatasetCode).ToArray();

        // OKX serves a book ladder, a tape, liquidations keyed by underlying, mark and index bars,
        // and its own per-instrument open-interest series.
        //
        // That last one is here because the first draft of this adapter DENIED it. rubik has two
        // routes whose names differ by one word: open-interest-VOLUME is keyed by currency and sums
        // every contract on the coin, while open-interest-HISTORY takes an instId and answers this
        // instrument's own figure. Finding the first and concluding there was no series is exactly
        // the mistake this assertion now prevents from coming back.
        Assert.Contains("depth", okx);
        Assert.Contains("trades", okx);
        Assert.Contains("liquidations", okx);
        Assert.Contains("open_interest", okx);
        Assert.Contains("candles_mark", okx);
        Assert.Contains("candles_index", okx);

        // INTX publishes NEITHER a book ladder (its /quote is top-of-book only) nor a trades route
        // (404). Both absences are the venue's, and this is the test that says so out loud so the
        // next reader does not go looking for work that does not exist.
        Assert.DoesNotContain("depth", intx);
        Assert.DoesNotContain("trades", intx);
        Assert.DoesNotContain("liquidations", intx);

        // Nor mark or index candles — and this one is a refusal rather than an absence. The candle
        // route accepts type=MARK and type=INDEX and answers 200, but the bars that come back are
        // IDENTICAL to type=TRADE, to the last decimal. Storing them would be one measurement filed
        // under three names, which reads in the grid as three independent prices agreeing perfectly.
        Assert.DoesNotContain("candles_mark", intx);
        Assert.DoesNotContain("candles_index", intx);

        // MEXC serves both, plus a settled-funding series 1 618 payments deep. No open-interest
        // history (contract/openInterest/{symbol} answers 403 with an HTML body while every other
        // route answers 200 from the same address), no liquidation flag on its tape, and no mark or
        // index candles — it publishes a current fairPrice with no series behind it.
        Assert.Contains("depth", mexc);
        Assert.Contains("trades", mexc);
        Assert.Contains("funding", mexc);
        Assert.DoesNotContain("open_interest", mexc);
        Assert.DoesNotContain("liquidations", mexc);
        Assert.DoesNotContain("candles_mark", mexc);
        Assert.DoesNotContain("candles_index", mexc);

        foreach (var codes in new[] { okx, intx, mexc })
        {
            Assert.Contains("snapshot", codes);
            Assert.Contains("candles", codes);
            // book_topn is socket-fed everywhere; a REST ladder is not one.
            Assert.DoesNotContain("book", codes);
        }
    }

    private sealed class RouteStub((string PathEndsWith, string Body)[] routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            // Last match wins, so a test can override one route by appending it.
            var body = routes.LastOrDefault(r => path.EndsWith(r.PathEndsWith, StringComparison.Ordinal)).Body;

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }
}

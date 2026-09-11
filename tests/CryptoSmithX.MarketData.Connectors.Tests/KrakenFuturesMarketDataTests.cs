using System.Net;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Drives the adapter against a stub handler that replays canonical Kraken Futures JSON (captured
/// from the live public API and trimmed), so the whole HTTP → JSON → mapping path is exercised with
/// no network. The assertions pin the parts that are easy to get wrong: the raw XBT spelling, the
/// PF_ scope filter, the funding relativisation, the null trade count and the depth bands.
/// </summary>
public sealed class KrakenFuturesMarketDataTests
{
    private const string BaseUrl = "https://futures.kraken.test";
    private const string ChartsUrl = "https://futures.kraken.test/api/charts/v1";

    private static KrakenFuturesMarketData Adapter() =>
        new(new KrakenFuturesClient(new HttpClient(new FixtureHandler()), BaseUrl, ChartsUrl));

    [Fact]
    public async Task Discovery_keeps_only_PF_perps_and_leaves_the_base_as_kraken_spells_it()
    {
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        // PF_XBTUSD and PF_SOLUSD survive; PI_ (inverse) and FI_ (dated) are dropped.
        Assert.Equal(new[] { "PF_XBTUSD", "PF_SOLUSD" }, instruments.Select(i => i.ExchangeSymbol).ToArray());

        var xbt = instruments.Single(i => i.ExchangeSymbol == "PF_XBTUSD");
        Assert.Equal("XBT", xbt.BaseAssetRaw);   // not "BTC": the alias table resolves it downstream
        Assert.Equal("USD", xbt.QuoteAssetRaw);
        Assert.Equal(1m, xbt.ContractMultiplier);
        Assert.Equal(1m, xbt.PriceStep);
        Assert.Equal(0.0001m, xbt.QtyStep);      // contractValueTradePrecision = 4
        Assert.Null(xbt.MinNotional);            // Kraken defines none
        Assert.Equal((short)1, xbt.FundingIntervalHours);
        Assert.Equal(InstrumentStatus.Trading, xbt.Status);
        Assert.Equal(new DateTimeOffset(2022, 3, 22, 13, 15, 36, TimeSpan.Zero), xbt.ListedAt);
        Assert.Contains("PF_XBTUSD", xbt.RawJson);
    }

    [Fact]
    public async Task Tickers_relativise_funding_by_mark_and_carry_no_book()
    {
        var tickers = await Adapter().GetTickersAsync(CancellationToken.None);

        Assert.Equal(new[] { "PF_XBTUSD", "PF_SOLUSD" }, tickers.Select(t => t.ExchangeSymbol).ToArray());

        var xbt = tickers.Single(t => t.ExchangeSymbol == "PF_XBTUSD");
        // Kraken's ticker fundingRate is absolute; the schema wants the fraction, i.e. divided by mark.
        Assert.Equal(0.4364457837055987 / 78873.23878806471, xbt.FundingRate!.Value, 12);
        Assert.Equal(608756216.3351, xbt.Turnover24h);   // volumeQuote, in the quote asset
        Assert.Equal(1942.3394, xbt.OpenInterest);       // in the base asset
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 21, 26, 7, 794, TimeSpan.Zero), xbt.ReceivedAt);
        Assert.Null(xbt.Depth);                          // the book is a separate call
    }

    [Fact]
    public async Task Candles_are_parsed_from_strings_drop_the_forming_bar_and_have_no_trade_count()
    {
        // `to` is the open of the fourth (still-forming) bar, so only the three closed bars survive.
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1788208200000);
        var from = DateTimeOffset.FromUnixTimeMilliseconds(1788208020000);

        var candles = await Adapter().GetCandles1mAsync("PF_XBTUSD", from, to, CancellationToken.None);

        Assert.Equal(3, candles.Count);
        Assert.All(candles, c => Assert.Null(c.TradeCount));   // Kraken reports none
        Assert.All(candles, c => Assert.Equal(0, c.OpenTime.Second));
        for (var i = 1; i < candles.Count; i++)
        {
            Assert.Equal(TimeSpan.FromMinutes(1), candles[i].OpenTime - candles[i - 1].OpenTime);
        }

        var first = candles[0];
        Assert.Equal(78976, first.Open);
        Assert.Equal(78958, first.Close);
        Assert.Equal(0.17820, first.Volume, 6);
    }

    [Fact]
    public async Task Funding_uses_the_relative_rate_and_windows_to_the_range()
    {
        var from = new DateTimeOffset(2026, 8, 31, 18, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 8, 31, 20, 30, 0, TimeSpan.Zero);

        var rates = await Adapter().GetFundingHistoryAsync("PF_XBTUSD", from, to, CancellationToken.None);

        // 17:00 is before the window and 21:00 after it; 18/19/20 remain, oldest first.
        Assert.Equal(
            new[]
            {
                new DateTimeOffset(2026, 8, 31, 18, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 31, 19, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero),
            },
            rates.Select(r => r.FundingTime).ToArray());
        Assert.Equal(8.397133333333e-06, rates[0].Rate, 15);   // relativeFundingRate, used as-is
    }

    [Fact]
    public async Task Order_book_sums_cumulative_notional_within_each_band()
    {
        var depth = await Adapter().GetOrderBookAsync("PF_XBTUSD", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 21, 26, 9, 512, TimeSpan.Zero), depth!.At);

        // Deep book: every band is bounded by a level beyond it, so none is null, and each wider
        // band includes at least as much as the narrower one.
        Assert.True(depth.Bid10Bps > 0 && depth.Ask10Bps > 0);
        Assert.True(depth.Bid25Bps >= depth.Bid10Bps);
        Assert.True(depth.Bid50Bps >= depth.Bid25Bps);
        Assert.True(depth.Ask25Bps >= depth.Ask10Bps);
        Assert.True(depth.Ask50Bps >= depth.Ask25Bps);
    }

    [Fact]
    public async Task A_band_the_book_does_not_reach_past_stays_null()
    {
        var depth = await Adapter().GetOrderBookAsync("PF_THINUSD", CancellationToken.None);

        Assert.NotNull(depth);
        // mid = 100. The book has a level beyond 10 and 25 bps, so those sum; nothing lies beyond
        // 50 bps, so that band would be an undercount and is left null.
        Assert.Equal(199.98, depth!.Bid10Bps!.Value, 6);
        Assert.Equal(499.53, depth.Bid25Bps!.Value, 6);
        Assert.Null(depth.Bid50Bps);
        Assert.Equal(200.02, depth.Ask10Bps!.Value, 6);
        Assert.Equal(500.47, depth.Ask25Bps!.Value, 6);
        Assert.Null(depth.Ask50Bps);
    }

    [Fact]
    public async Task An_http_error_propagates_rather_than_being_swallowed()
    {
        // The adapter never retries or hides failures; the loop above is what counts them.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Adapter().GetOrderBookAsync("PF_ERRUSD", CancellationToken.None));
    }

    /// <summary>Replays a fixture per endpoint; routes the order book by symbol so the thin and
    /// error cases can be reached, and 500s for the error symbol.</summary>
    /// <summary>
    /// A negative <c>contractValueTradePrecision</c> means the step is COARSER than one, and the
    /// venue reports it on fifteen live instruments — PF_PEPEUSD, PF_SHIBUSD, PF_BONKUSD,
    /// PF_FLOKIUSD and PF_MOGUSD at -3, PF_TURBOUSD and PF_PUMPUSD at -2, eight more at -1. It is
    /// the ordinary way to quote a coin whose unit price is a millionth of a dollar.
    ///
    /// The loop this pins ran zero times on a negative precision and returned 1: a step a thousand
    /// times finer than the venue accepts, written into BOTH qty_step and min_qty, and passed by
    /// `CHECK (qty_step > 0)` in silence because 1 is a perfectly good positive number.
    ///
    /// The payload here is hand-written rather than captured, and deliberately so: the captured
    /// instruments.json holds no negative precision, so appending one would make it no longer a
    /// capture. The three values are the venue's own, read from its live response.
    /// </summary>
    [Theory]
    [InlineData(-3, 1000)]
    [InlineData(-2, 100)]
    [InlineData(-1, 10)]
    [InlineData(0, 1)]
    [InlineData(3, 0.001)]
    public async Task A_coarser_than_one_step_is_not_flattened_to_one(int precision, decimal expected)
    {
        var json = $$"""
            {"result":"success","instruments":[{
                "symbol":"PF_TESTUSD","type":"flexible_futures","tradeable":true,
                "underlying":"rr_testusd","tickSize":1e-8,
                "contractValueTradePrecision":{{precision}},
                "isin":"","contractSize":1,"marginLevels":[],"openingDate":"2024-01-01T00:00:00.000Z"
            }]}
            """;

        var market = new KrakenFuturesMarketData(
            new KrakenFuturesClient(new HttpClient(new OneShotHandler(json)), BaseUrl, ChartsUrl));

        var instruments = await market.GetInstrumentsAsync(CancellationToken.None);
        var only = Assert.Single(instruments);

        Assert.Equal(expected, only.QtyStep);
        // The call site hands the same value to MinQty, so the defect was always two columns.
        Assert.Equal(expected, only.MinQty);
    }

    private sealed class OneShotHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private static readonly string FixtureDir =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "kraken");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var query = uri.Query;

            if (path.EndsWith("/instruments", StringComparison.Ordinal))
            {
                return Json("instruments.json");
            }

            if (path.EndsWith("/tickers", StringComparison.Ordinal))
            {
                return Json("tickers.json");
            }

            if (path.EndsWith("/historicalfundingrates", StringComparison.Ordinal))
            {
                return Json("funding.json");
            }

            if (path.Contains("/trade/", StringComparison.Ordinal) && path.EndsWith("/1m", StringComparison.Ordinal))
            {
                return Json("candles.json");
            }

            if (path.EndsWith("/orderbook", StringComparison.Ordinal))
            {
                if (query.Contains("PF_ERRUSD", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return Json(query.Contains("PF_THINUSD", StringComparison.Ordinal) ? "orderbook_thin.json" : "orderbook.json");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string fixture)
        {
            var body = File.ReadAllText(Path.Combine(FixtureDir, fixture));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

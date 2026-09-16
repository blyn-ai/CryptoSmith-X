using System.Net;
using System.Text;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Synthetix;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Synthetix — second in the DEX order, and the venue whose measurement overturned the catalogue: 0057
/// seeded it as an oracle pool, and what answers at papi.synthetix.io is an order book. Fixtures in
/// <c>Fixtures/synthetix</c> are trimmed copies of live responses taken 2026-09-13.
///
/// The trap on this venue is one word: the field every other route here would call the funding rate is,
/// on <c>getMarketPrices</c>, the ESTIMATE for the period still accruing.
/// </summary>
public sealed class SynthetixPerpTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetix", name));

    private static SynthetixPerpMarketData Snx(List<string>? actions = null) =>
        new(new SynthetixClient(new HttpClient(new Stub(actions)), "https://papi.test"));

    [Fact]
    public async Task Every_read_is_one_post_to_info_with_the_action_in_the_body_and_a_named_agent()
    {
        var actions = new List<string>();
        await Snx(actions).GetTickersAsync(CancellationToken.None);

        Assert.Equal(["getMarketPrices"], actions);
    }

    [Fact]
    public async Task The_rate_in_force_is_the_settled_one_and_the_bulk_fundingRate_is_the_estimate()
    {
        // getMarketPrices.fundingRate is 0.0000124476766622999 — to the last digit the number
        // getFundingRate calls estimatedFundingRate. The settled rate is 0.00001229. Storing the first as
        // the rate in force would put a forecast where every other venue puts a measurement.
        var snx = Snx();
        await snx.GetOrderBookAsync("BTC-USDT", CancellationToken.None);

        var t = (await snx.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USDT");

        Assert.Equal(0.00001229, t.FundingRate!.Value, 12);
        Assert.Equal(0.0000124476766622999, t.FundingRatePredicted!.Value, 15);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_304_400_000), t.NextFundingAt);
    }

    [Fact]
    public async Task Volume_and_open_interest_are_base_units_and_turnover_is_the_quote_figure()
    {
        // 4.29913 BTC and 331 780.88 USDT — the same flow in two currencies, at the day's price.
        var t = (await Snx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USDT");

        Assert.Equal(4.29913, t.Volume24hBase!.Value, 6);
        Assert.Equal(331_780.87751, t.Turnover24h!.Value, 4);
        Assert.Equal(0.45942, t.OpenInterest!.Value, 6);
        Assert.InRange(t.Turnover24h!.Value / t.Volume24hBase!.Value, 70_000, 85_000);

        Assert.Equal(76_763, t.MarkPrice!.Value, 6);
        Assert.Equal(76_794, t.IndexPrice!.Value, 6);
        Assert.Equal(76_766, t.LastPrice!.Value, 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_301_726_158), t.VenueTs);
    }

    [Fact]
    public async Task Bid_ask_and_sizes_come_from_one_book_frame_so_a_size_belongs_to_its_price()
    {
        var snx = Snx();

        // Before the book: the bulk route's own best bid and ask, and no sizes — not a size borrowed from
        // some other moment.
        var before = (await snx.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USDT");
        Assert.Equal(76_755, before.BidPrice!.Value, 6);
        Assert.Null(before.BidSize);

        var depth = await snx.GetOrderBookAsync("BTC-USDT", CancellationToken.None);
        Assert.NotNull(depth);

        var after = (await snx.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USDT");
        Assert.Equal(76_755, after.BidPrice!.Value, 6);
        Assert.Equal(0.45267, after.BidSize!.Value, 6);
        Assert.Equal(76_785, after.AskPrice!.Value, 6);
        Assert.Equal(0.45735, after.AskSize!.Value, 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_301_725_286), after.LastTradeAt);
    }

    [Fact]
    public async Task Discovery_reads_the_interval_the_venue_publishes_beside_each_rate()
    {
        var btc = (await Snx().GetInstrumentsAsync(CancellationToken.None)).Single(i => i.ExchangeSymbol == "BTC-USDT");

        Assert.Equal("BTC", btc.BaseAssetRaw);
        Assert.Equal("USDT", btc.QuoteAssetRaw);
        Assert.Equal(1m, btc.ContractMultiplier);
        Assert.Equal((short)1, btc.FundingIntervalHours);
        Assert.Equal(InstrumentStatus.Trading, btc.Status);

        // "0" means the venue sets no minimum, which is not a minimum of zero.
        Assert.Null(btc.MinNotional);
    }

    [Fact]
    public void Liquidations_and_open_interest_history_are_not_claimed()
    {
        // Neither is published: the public tape carries no liquidation marker, the only liquidation
        // stream is an account's own, and open interest has no series behind the current figure.
        var caps = Snx().Capabilities.Select(c => c.DatasetCode).ToArray();

        Assert.DoesNotContain("liquidations", caps);
        Assert.DoesNotContain("open_interest", caps);
        Assert.Contains("depth", caps);
        Assert.Contains("funding", caps);
    }

    [Fact]
    public async Task The_tape_side_is_the_takers_and_carries_no_invented_type()
    {
        using var clock = FixtureClock.Freeze();
        var snx = Snx();
        await snx.GetOrderBookAsync("BTC-USDT", CancellationToken.None);

        var trades = snx.DrainTrades();
        Assert.Equal(5, trades.Count);
        Assert.Equal("sell", trades.Single(t => t.VenueUid == "2099109468984860672").TakerSide);
        Assert.All(trades, t => Assert.Null(t.TradeType));
    }

    [Fact]
    public async Task A_bar_carries_base_quote_and_count_and_the_settled_series_reads_back()
    {
        var snx = Snx();
        var bars = await snx.GetCandles1mAsync("BTC-USDT",
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_301_500_000),
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_301_900_000), CancellationToken.None);

        var c = bars.Single(b => b.OpenTime == DateTimeOffset.FromUnixTimeMilliseconds(1_789_301_700_000));
        Assert.Equal(0.00245, c.Volume, 6);
        Assert.Equal(188.08126, c.VolumeQuote!.Value, 5);
        Assert.Equal(2, c.TradeCount);

        var funding = await snx.GetFundingHistoryAsync("BTC-USDT", DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(3, funding.Count);
        Assert.Equal(0.00001229, funding.Single(f => f.FundingTime == DateTimeOffset.FromUnixTimeMilliseconds(1_789_300_800_000)).Rate, 12);
    }

    private sealed class Stub(List<string>? actions) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/v1/info", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("CryptoSmithX", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);

            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var action = doc.RootElement.GetProperty("params").GetProperty("action").GetString()!;
            actions?.Add(action);

            var body = action switch
            {
                "getMarkets" => Fixture("getMarkets.json"),
                "getMarketPrices" => Fixture("getMarketPrices.json"),
                "getFundingRate" => Fixture("getFundingRate_BTC-USDT.json"),
                "getOrderbook" => Fixture("getOrderbook_BTC-USDT.json"),
                "getLastTrades" => Fixture("getLastTrades_BTC-USDT.json"),
                "getCandles" => Fixture("getCandles_BTC-USDT.json"),
                "getFundingRateHistory" => Fixture("getFundingRateHistory_BTC-USDT.json"),
                _ => null,
            };

            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}

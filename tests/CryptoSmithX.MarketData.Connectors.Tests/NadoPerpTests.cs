using System.Net;
using System.Text;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Nado;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Nado on Ink L2 — third in the DEX order, taken instead of Vertex, which closed in July 2025. Fixtures
/// in <c>Fixtures/nado</c> are trimmed copies of live archive and gateway responses taken 2026-09-13.
///
/// Four places here where a wrong number would look reasonable: prices and sizes as x18 integers; a
/// funding rate restated per 24 hours beside a series that settles hourly; a minimum "size" that is a
/// quote notional; and a liquidation that arrives as two events for one event.
/// </summary>
public sealed class NadoPerpTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nado", name));

    private static NadoPerpMarketData Nado(List<string>? seen = null) =>
        new(new NadoClient(new HttpClient(new Stub(seen)), "https://archive.prod.nado.test"));

    private static async Task<NadoPerpMarketData> Discovered(List<string>? seen = null)
    {
        var nado = Nado(seen);
        await nado.GetInstrumentsAsync(CancellationToken.None);
        return nado;
    }

    [Fact]
    public async Task The_perp_suffix_comes_off_the_base_and_the_quote_stays_usdt0()
    {
        // "BTC-PERP" names the product type; left on, the venue would sit on no BTC page. USDT0 is the
        // bridged USDT on Ink, a distinct token, and is not called USDT.
        var btc = (await Nado().GetInstrumentsAsync(CancellationToken.None)).Single(i => i.ExchangeSymbol == "BTC-PERP_USDT0");

        Assert.Equal("BTC", btc.BaseAssetRaw);
        Assert.Equal("USDT0", btc.QuoteAssetRaw);
    }

    [Fact]
    public async Task Specs_are_x18_and_min_size_is_a_quote_notional()
    {
        var btc = (await Nado().GetInstrumentsAsync(CancellationToken.None)).Single(i => i.ExchangeSymbol == "BTC-PERP_USDT0");

        Assert.Equal(1m, btc.PriceStep);
        Assert.Equal(0.00005m, btc.QtyStep);

        // min_size is 100 × 10^18 — a hundred USDT0 of notional, not a hundred bitcoin.
        Assert.Equal(100m, btc.MinNotional);
        Assert.Null(btc.MinQty);
        Assert.Equal((short)1, btc.FundingIntervalHours);
    }

    [Fact]
    public async Task Volume_and_open_interest_are_base_units_with_the_venues_own_notionals()
    {
        var t = (await Nado().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-PERP_USDT0");

        Assert.Equal(1_021.63085, t.Volume24hBase!.Value, 5);
        Assert.Equal(78_739_026.9701858, t.Turnover24h!.Value, 3);
        Assert.Equal(255.0367, t.OpenInterest!.Value, 4);
        Assert.NotNull(t.OiQuote);
        Assert.Equal(76_613, t.LastPrice!.Value, 6);
        Assert.Equal(76_651.35, t.IndexPrice!.Value, 2);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_308_000), t.NextFundingAt);
    }

    [Fact]
    public async Task The_rate_in_force_is_the_realised_hourly_one_not_the_24h_restatement()
    {
        // contracts.funding_rate is 0.000201 in 24-hour terms; the realised series is 8.37e-6 an HOUR. The
        // Funding column scales by the interval, so storing the first as an hourly rate would print a daily
        // funding twenty-four times too large.
        var nado = await Discovered();
        await nado.GetFundingHistoryAsync("BTC-PERP_USDT0", DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, CancellationToken.None);

        var t = (await nado.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-PERP_USDT0");
        Assert.Equal(0.000008372447963973, t.FundingRate!.Value, 15);
    }

    [Fact]
    public async Task Bid_ask_and_sizes_are_one_book_frame_and_the_book_keeps_its_own_clock()
    {
        var nado = await Discovered();
        Assert.Null((await nado.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-PERP_USDT0").BidPrice);

        var depth = await nado.GetOrderBookAsync("BTC-PERP_USDT0", CancellationToken.None);
        Assert.NotNull(depth);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_789_306_395_484), depth.At);
        Assert.NotNull(depth.Bid25Bps);

        var t = (await nado.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-PERP_USDT0");
        Assert.Equal(76_616, t.BidPrice!.Value, 6);
        Assert.Equal(2.77985, t.BidSize!.Value, 6);
        Assert.Equal(76_617, t.AskPrice!.Value, 6);
        Assert.Equal(0.7848, t.AskSize!.Value, 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_306_388), t.LastTradeAt);
    }

    [Fact]
    public void The_tape_is_not_claimed_because_every_match_is_two_rows_with_fees_folded_in()
    {
        var caps = Nado().Capabilities.Select(c => c.DatasetCode).ToArray();

        Assert.DoesNotContain("trades", caps);
        Assert.Contains("liquidations", caps);
        Assert.Contains("depth", caps);
    }

    [Fact]
    public async Task One_liquidation_is_counted_once_though_both_sides_report_it()
    {
        // Submission 81035362 moved this product's balances 0 → 0.00655 (the liquidator) and 0.01315 → 0.0066
        // (the liquidatee): one liquidation of 0.00655 BTC reported twice. Two other submissions in the fixture
        // touched no BTC-PERP balance at all and count nothing.
        var nado = await Discovered();
        var buckets = await nado.GetLiquidationVolumeAsync(
            "BTC-PERP_USDT0",
            DateTimeOffset.FromUnixTimeSeconds(1_789_290_000),
            DateTimeOffset.FromUnixTimeSeconds(1_789_307_000),
            CancellationToken.None);

        var hour = DateTimeOffset.FromUnixTimeSeconds(1_789_297_364 / 3600 * 3600);
        Assert.Equal(0.00655, buckets.Single(b => b.BucketTime == hour).Volume, 9);

        // The page was shorter than asked for, so the whole window was covered: other hours are counted zeros.
        Assert.Equal(0d, buckets.Single(b => b.BucketTime == hour + TimeSpan.FromHours(2)).Volume);
    }

    [Fact]
    public async Task A_bar_is_read_off_x18_integers()
    {
        var nado = await Discovered();
        var bars = await nado.GetCandles1mAsync("BTC-PERP_USDT0",
            DateTimeOffset.FromUnixTimeSeconds(1_789_306_000), DateTimeOffset.FromUnixTimeSeconds(1_789_306_500), CancellationToken.None);

        var c = bars.Single(b => b.OpenTime == DateTimeOffset.FromUnixTimeSeconds(1_789_306_380));
        Assert.Equal(76_608, c.Open, 6);
        Assert.Equal(76_613, c.Close, 6);
        Assert.Equal(0.13775, c.Volume, 9);
    }

    [Fact]
    public async Task The_gateway_is_the_archives_documented_sibling_host()
    {
        var seen = new List<string>();
        var nado = await Discovered(seen);
        await nado.GetOrderBookAsync("BTC-PERP_USDT0", CancellationToken.None);

        Assert.Contains(seen, u => u.StartsWith("https://gateway.prod.nado.test/v1/query?type=symbols", StringComparison.Ordinal));
        Assert.Contains(seen, u => u.StartsWith("https://gateway.prod.nado.test/v2/orderbook", StringComparison.Ordinal));
        Assert.Contains(seen, u => u.StartsWith("https://archive.prod.nado.test/v2/contracts", StringComparison.Ordinal));
    }

    private sealed class Stub(List<string>? seen) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            seen?.Add(uri.ToString());

            string? body = null;
            if (request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var kind = doc.RootElement.EnumerateObject().First().Name;
                body = kind switch
                {
                    "candlesticks" => Fixture("candlesticks_BTC.json"),
                    "funding_rate_history" => Fixture("funding_rate_history_BTC.json"),
                    "events" => Fixture("liquidations_BTC.json"),
                    _ => null,
                };
            }
            else
            {
                var p = uri.AbsolutePath + uri.Query;
                body = p switch
                {
                    _ when p.StartsWith("/v2/contracts", StringComparison.Ordinal) => Fixture("contracts.json"),
                    _ when p.StartsWith("/v1/query?type=symbols", StringComparison.Ordinal) => Fixture("symbols.json"),
                    _ when p.StartsWith("/v2/orderbook", StringComparison.Ordinal) => Fixture("orderbook_BTC.json"),
                    _ when p.StartsWith("/v2/trades", StringComparison.Ordinal) => Fixture("trades_BTC.json"),
                    _ => null,
                };
            }

            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}

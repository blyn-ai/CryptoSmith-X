using System.Net;
using System.Text;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Gmx;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// GMX v2 on Arbitrum — fourth in the DEX order, the first oracle venue after Avantis. Fixtures in
/// <c>Fixtures/gmx</c> are trimmed copies of live arbitrum.gmxapi.io responses taken 2026-09-13.
///
/// Places where a wrong number would look reasonable: three precisions (1e30 USD, token decimals, and trade
/// prices per RAW token unit); a funding sign that is the paying side's, not the long's; a pool that is one of
/// several for the same asset; and a quote that is computed, so its function has to be the venue's.
/// </summary>
public sealed class GmxPerpTests
{
    private const string Btc = "BTC/USD [BTC-USDC]";
    private const string Eth = "ETH/USD [WETH-USDC]";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gmx", name));

    private static GmxPerpMarketData Gmx(List<string>? seen = null) =>
        new(new GmxClient(new HttpClient(new Stub(seen)), "https://arbitrum.gmxapi.test"));

    [Fact]
    public async Task A_pool_is_an_instrument_named_for_its_asset_and_a_swap_pool_is_none()
    {
        var instruments = await Gmx().GetInstrumentsAsync(CancellationToken.None);

        var btc = instruments.Single(i => i.ExchangeSymbol == Btc);
        Assert.Equal("BTC", btc.BaseAssetRaw);
        Assert.Equal("USD", btc.QuoteAssetRaw);
        // minPositionSizeUsd is 1e30: one dollar, not 10^30 of anything.
        Assert.Equal(1m, btc.MinNotional);
        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol.StartsWith("SWAP-ONLY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ticker_prices_are_per_whole_token_at_1e30_and_the_index_is_the_oracle_mid()
    {
        var t = (await Gmx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == Btc);

        Assert.Equal(77_087.2980372380325, t.IndexPrice!.Value, 6);
        Assert.Equal(77_087.2980372380325, t.MarkPrice!.Value, 6);
    }

    [Fact]
    public async Task Open_interest_is_tokens_at_the_index_tokens_decimals_beside_the_venues_usd()
    {
        var t = (await Gmx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == Btc);

        // 9 904 293 019 + 10 053 825 377 at BTC's 8 decimals.
        Assert.Equal(199.58118396, t.OpenInterest!.Value, 8);
        Assert.Equal(7_599_325.28 + 7_307_964.92, t.OiQuote!.Value, 0);
    }

    [Fact]
    public async Task Funding_is_what_a_long_pays_per_hour_and_the_venue_signs_the_payer_negative()
    {
        // fundingRateLong +4.2196e-6: longs RECEIVE (SDK getFundingFactorPerPeriod negates the paying side).
        // The column means what a long pays, so it is written negative.
        var t = (await Gmx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == Btc);

        Assert.Equal(-4.2196439498963415e-6, t.FundingRate!.Value, 15);
        Assert.Null(t.NextFundingAt);
    }

    [Fact]
    public async Task Turnover_comes_from_pairs_in_both_units()
    {
        var t = (await Gmx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == Btc);

        Assert.Equal(3_378_842.6911071427, t.Turnover24h!.Value, 4);
        Assert.Equal(43.835513920288186, t.Volume24hBase!.Value, 9);
    }

    [Fact]
    public async Task The_side_that_rebalances_the_pool_is_quoted_through_the_oracle_and_capacity_is_base_units()
    {
        var t = (await Gmx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == Btc);

        // Shorts are the heavier side (100.54 against 99.04 BTC), so a $10 000 long improves the balance and the
        // pool pays for it: the ASK sits under the oracle, by about 0.3 bps. Not a crossed quote — the short that
        // worsens the balance pays more than the long is paid, so the bid stays under the ask.
        Assert.True(t.BidPrice < t.AskPrice, $"{t.BidPrice} / {t.AskPrice}");
        Assert.True(t.AskPrice < t.IndexPrice, $"{t.AskPrice} / {t.IndexPrice}");
        Assert.InRange((t.IndexPrice!.Value - t.BidPrice!.Value) / t.IndexPrice.Value * 10_000, 0.2, 1.0);
        // availableLiquidity 95 928 990 USD on the short side, at the mid.
        Assert.Equal(95_928_990.06 / 77_087.2980372, t.BidSize!.Value, 2);
    }

    [Fact]
    public async Task Depth_grows_with_the_threshold_and_stops_at_the_pools_capacity()
    {
        var d = await Gmx().GetOrderBookAsync(Btc, CancellationToken.None);

        Assert.NotNull(d);
        Assert.True(d!.Ask10Bps <= d.Ask25Bps && d.Ask25Bps <= d.Ask50Bps, $"{d.Ask10Bps} {d.Ask25Bps} {d.Ask50Bps}");
        Assert.True(d.Ask50Bps <= 96_395_449.36);
        Assert.True(d.ReachAskBps > 0);
    }

    [Fact]
    public void The_impact_function_matches_the_sdk_by_hand()
    {
        // Balanced 1 000 / 1 000 in USD, exponent 2. Opening $100 long crosses over: positive 0^2 × f+ = 0,
        // negative 100^2 × 1e-6 = 0.01 → impact −0.01, so the ask is 100 × (100 + 0.01) / 100 = 100.01.
        var balanced = Model(longOi: 1_000, shortOi: 1_000);
        Assert.Equal(-0.01, balanced.ImpactUsd(100, isLong: true), 12);
        Assert.Equal(100.01, balanced.Ask(100), 10);

        // Longs heavy by 100; a $100 short restores balance: positive 100^2 × 5e-7 = 0.005, under the cap of
        // min(0.004, 0.005) × 100 = 0.4 — paid to the trader, so the bid is above the oracle.
        var heavy = Model(longOi: 1_100, shortOi: 1_000);
        Assert.Equal(0.005, heavy.ImpactUsd(100, isLong: false), 12);
        Assert.Equal(100.005, heavy.Bid(100), 10);

        // On the balanced book a size costs size/100 bps, so 1 bps is reached at $100.
        Assert.Equal(100, balanced.SizeWithin(1, isLong: true, capacityUsd: 1_000_000), 1);
    }

    [Fact]
    public void A_positive_impact_is_capped_by_the_smaller_max_factor()
    {
        // A huge rebalancing reward on a tiny order: capped at min(0.004, 0.005) × size.
        var model = Model(longOi: 1_000_000, shortOi: 0) with { FactorPositive = 1 };
        Assert.Equal(0.4, model.ImpactUsd(100, isLong: false), 12);
    }

    [Fact]
    public void A_trade_price_is_per_raw_token_unit_and_needs_the_decimals_back()
    {
        var trade = Trades().First(t => t.Id.StartsWith("0x8c7ae003", StringComparison.Ordinal));

        var e = GmxPerpMarketData.ToEvent(trade, Btc, decimals: 8)!;

        Assert.Equal(77_057.0293036249, e.Price, 6);
        Assert.Equal(0.0000131, e.Qty, 12);
        // A market decrease of a short buys back from the pool.
        Assert.Equal("buy", e.TakerSide);
        Assert.Equal("fill", e.TradeType);
    }

    [Fact]
    public void Order_type_seven_is_the_venues_own_liquidation_mark()
    {
        Assert.Equal("liquidation", GmxPerpMarketData.TradeType(7));
        Assert.Equal("sell", GmxPerpMarketData.TakerSide(Trades()[0] with { OrderType = 7, IsLong = true }));
        Assert.Equal("buy", GmxPerpMarketData.TakerSide(Trades()[0] with { OrderType = 2, IsLong = true }));
    }

    [Fact]
    public async Task Liquidations_are_base_units_per_pool_and_a_covered_hour_is_a_counted_zero()
    {
        var gmx = Gmx();
        var from = DateTimeOffset.FromUnixTimeSeconds(1_789_280_000);
        var to = DateTimeOffset.FromUnixTimeSeconds(1_789_310_000);

        var btc = await gmx.GetLiquidationVolumeAsync(Btc, from, to, CancellationToken.None);
        var eth = await gmx.GetLiquidationVolumeAsync(Eth, from, to, CancellationToken.None);

        // 49 659 500 at 8 decimals, in the hour of 1 789 288 230.
        var hour = DateTimeOffset.FromUnixTimeSeconds(1_789_288_230 / 3600 * 3600);
        Assert.Equal(0.496595, btc.Single(b => b.BucketTime == hour).Volume, 9);
        Assert.Equal("base", btc[0].VolumeUnit);
        Assert.Equal(0d, btc.Single(b => b.BucketTime == hour + TimeSpan.FromHours(3)).Volume);

        // Three ETH liquidations in one hour, at 18 decimals, and none of BTC's or BERA's.
        var ethHour = DateTimeOffset.FromUnixTimeSeconds(1_789_305_837 / 3600 * 3600);
        Assert.Equal(1.456661105271078467 + 0.189856695006132040 + 6.278367591268612682,
            eth.Single(b => b.BucketTime == ethHour).Volume, 9);
    }

    [Fact]
    public async Task One_liquidation_walk_serves_every_pool()
    {
        var seen = new List<string>();
        var gmx = Gmx(seen);
        var from = DateTimeOffset.FromUnixTimeSeconds(1_789_280_000);
        var to = DateTimeOffset.FromUnixTimeSeconds(1_789_310_000);

        await gmx.GetLiquidationVolumeAsync(Btc, from, to, CancellationToken.None);
        await gmx.GetLiquidationVolumeAsync(Eth, from, to, CancellationToken.None);

        Assert.Single(seen, u => u.EndsWith("/trades/search#liquidations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_market_the_venues_search_gives_up_on_does_not_fail_the_snapshot()
    {
        // arbitrum.gmxapi.io answers a per-market search on a quiet pool with HTTP 500 after ~10.5 s.
        var gmx = new GmxPerpMarketData(new GmxClient(new HttpClient(new Stub(null, failMarketSearch: true)), "https://arbitrum.gmxapi.test"));

        var tickers = await gmx.GetTickersAsync(CancellationToken.None);

        Assert.Contains(tickers, t => t.ExchangeSymbol == Btc && t.BidPrice is not null);
    }

    [Fact]
    public async Task Oracle_bars_are_closed_minutes_inside_the_window()
    {
        var bars = await Gmx().GetPriceCandles1mAsync(Btc, "index",
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_309_740_000), DateTimeOffset.FromUnixTimeMilliseconds(1_789_309_920_000), CancellationToken.None);

        Assert.Equal([1_789_309_740_000L, 1_789_309_800_000L, 1_789_309_860_000L], bars.Select(b => b.OpenTime.ToUnixTimeMilliseconds()));
        Assert.Equal(77_081.93, bars[^1].Close, 6);
        Assert.Empty(await Gmx().GetPriceCandles1mAsync(Btc, "mark",
            DateTimeOffset.FromUnixTimeMilliseconds(1_789_309_740_000), DateTimeOffset.FromUnixTimeMilliseconds(1_789_309_920_000), CancellationToken.None));
    }

    private static GmxImpact Model(double longOi, double shortOi) => new(
        MinPrice: 100, MaxPrice: 100, LongOiUsd: longOi, ShortOiUsd: shortOi, TokensForBalance: false,
        FactorPositive: 5e-7, FactorNegative: 1e-6, ExponentPositive: 2, ExponentNegative: 2,
        MaxFactorPositive: 0.004, MaxFactorNegative: 0.005, HasVirtualInventory: false, VirtualInventoryUsd: 0);

    private static IReadOnlyList<GmxTrade> Trades() =>
        JsonSerializer.Deserialize<GmxTradesPage>(Fixture("trades.json"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Trades!;

    private sealed class Stub(List<string>? seen, bool failMarketSearch = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string? body;
            if (request.Method == HttpMethod.Post)
            {
                var text = await request.Content!.ReadAsStringAsync(ct);
                if (failMarketSearch && text.Contains("marketsDirections", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var liquidationsOnly = text.Contains("\"orderType\":7", StringComparison.Ordinal)
                                       && !text.Contains("\"orderType\":2", StringComparison.Ordinal);
                seen?.Add(request.RequestUri + (liquidationsOnly ? "#liquidations" : "#tape"));
                body = liquidationsOnly ? Fixture("liquidations.json") : Fixture("trades.json");
            }
            else
            {
                seen?.Add(request.RequestUri.ToString());
                body = path switch
                {
                    "/v1/markets" => Fixture("markets.json"),
                    "/v1/markets/tickers" => Fixture("tickers.json"),
                    "/v1/markets/info" => Fixture("markets_info.json"),
                    "/v1/pairs" => Fixture("pairs.json"),
                    "/v1/tokens" => Fixture("tokens.json"),
                    "/v1/prices/ohlcv" => Fixture("ohlcv_BTC.json"),
                    _ => null,
                };
            }

            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}

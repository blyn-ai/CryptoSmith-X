using System.Net;
using System.Text;
using CryptoSmithX.MarketData.Connectors.Dydx;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// dYdX v4 — the first DEX, and the first whose bid, ask, sizes and depth are measured off a real
/// book. Fixtures in <c>Fixtures/dydx</c> are trimmed copies of live indexer responses taken
/// 2026-09-13.
///
/// The indexer spreads one market across five routes, and each unit on them is a place a wrong
/// number would look reasonable: open interest in the base asset, 24h volume in USD, funding per
/// HOUR, and an open-interest figure that belongs to the hour before the bar that carries it.
/// </summary>
public sealed class DydxPerpTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dydx", name));

    private static DydxPerpMarketData Dydx(List<string>? seen = null) =>
        new(new DydxClient(new HttpClient(new Stub(seen)), "https://indexer.test"));

    [Fact]
    public async Task A_market_being_wound_down_is_delisted_and_an_active_one_trades()
    {
        var instruments = await Dydx().GetInstrumentsAsync(CancellationToken.None);

        Assert.Equal(InstrumentStatus.Trading, instruments.Single(i => i.ExchangeSymbol == "BTC-USD").Status);

        // FINAL_SETTLEMENT is terminal: 218 of 296 markets carried it when this was captured, and none
        // of them will trade again.
        Assert.Equal(InstrumentStatus.Delisted, instruments.Single(i => i.ExchangeSymbol == "MATIC-USD").Status);
    }

    [Fact]
    public async Task The_ticker_splits_into_the_venues_own_base_and_a_usd_quote()
    {
        // The page groups by asset family; a base that did not come out as BTC would put this venue
        // on no BTC page at all, silently.
        var btc = (await Dydx().GetInstrumentsAsync(CancellationToken.None)).Single(i => i.ExchangeSymbol == "BTC-USD");

        Assert.Equal("BTC", btc.BaseAssetRaw);
        Assert.Equal("USD", btc.QuoteAssetRaw);
        Assert.Equal(1m, btc.ContractMultiplier);
        Assert.Equal(1m, btc.PriceStep);
        Assert.Equal(0.0001m, btc.QtyStep);

        // Funding settles hourly; historicalFunding has one row per hour.
        Assert.Equal((short)1, btc.FundingIntervalHours);
    }

    [Fact]
    public async Task Open_interest_is_base_units_and_volume_is_usd()
    {
        // Measured against the venue itself: 24 hourly bars summed to 789 835 usdVolume beside a
        // volume24H of 750 651, and 200 BTC of open interest is about fifteen million dollars at the
        // price, not two hundred.
        var t = (await Dydx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USD");

        Assert.Equal(200.3857, t.OpenInterest!.Value, 6);
        Assert.Equal(750_735.811, t.Turnover24h!.Value, 3);
    }

    [Fact]
    public async Task Mark_and_index_are_both_the_oracle_price_because_the_protocol_uses_it_for_both()
    {
        var t = (await Dydx().GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USD");

        Assert.Equal(76_640.57188, t.MarkPrice!.Value, 5);
        Assert.Equal(t.MarkPrice, t.IndexPrice);

        // nextFundingRate is the NEXT hour's rate, and never the rate in force.
        Assert.Equal(-0.00000077, t.FundingRatePredicted!.Value, 12);
        Assert.Null(t.FundingRate);
    }

    [Fact]
    public async Task Bid_and_ask_come_from_the_book_and_are_empty_until_the_book_is_read()
    {
        // The bulk route carries no quote. Before the depth pass the columns are null — not the oracle
        // price standing in for an order nobody placed.
        var dydx = Dydx();
        var before = (await dydx.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USD");
        Assert.Null(before.BidPrice);
        Assert.Null(before.AskPrice);
        Assert.Null(before.LastPrice);

        var depth = await dydx.GetOrderBookAsync("BTC-USD", CancellationToken.None);
        Assert.NotNull(depth);
        Assert.NotNull(depth.Bid50Bps);

        var after = (await dydx.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USD");
        Assert.Equal(76_613, after.BidPrice!.Value, 6);
        Assert.Equal(0.0197, after.BidSize!.Value, 6);
        Assert.Equal(76_663, after.AskPrice!.Value, 6);
        Assert.Equal(0.1888, after.AskSize!.Value, 6);

        // The last price and its instant are the newest print on the tape.
        Assert.Equal(76_657, after.LastPrice!.Value, 6);
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T11:25:21.067Z"), after.LastTradeAt);

        // 24h base volume, summed from the venue's own hourly bars.
        Assert.Equal(1.2065 + 0.83 + 0.7638, after.Volume24hBase!.Value, 6);
    }

    [Fact]
    public async Task The_rate_in_force_is_the_newest_settled_row_once_the_funding_pass_has_run()
    {
        var dydx = Dydx();
        var rows = await dydx.GetFundingHistoryAsync(
            "BTC-USD", DateTimeOffset.Parse("2026-09-13T00:00:00Z"), DateTimeOffset.Parse("2026-09-13T12:00:00Z"),
            CancellationToken.None);

        Assert.Equal(3, rows.Count);

        var t = (await dydx.GetTickersAsync(CancellationToken.None)).Single(x => x.ExchangeSymbol == "BTC-USD");
        Assert.Equal(0d, t.FundingRate);
    }

    [Fact]
    public async Task A_bars_starting_open_interest_closes_the_hour_before_it()
    {
        // startingOpenInterest is the OI at the instant the bar opened — the closing OI of the previous
        // hour. Stamped on the bar that carries it, every value would sit one hour late.
        var buckets = await Dydx().GetOpenInterestHistoryAsync(
            "BTC-USD", DateTimeOffset.Parse("2026-09-13T08:00:00Z"), DateTimeOffset.Parse("2026-09-13T11:30:00Z"),
            CancellationToken.None);

        var tenOClock = buckets.Single(b => b.BucketTime == DateTimeOffset.Parse("2026-09-13T10:00:00Z"));
        Assert.Equal(200.4689, tenOClock.Close, 6);
        Assert.Equal(3600, tenOClock.IntervalSeconds);
    }

    [Fact]
    public async Task Liquidations_are_counted_off_the_tape_and_a_covered_hour_with_none_is_zero()
    {
        // The two LIQUIDATED prints in the fixture sit in the 11:00 hour and sum to 0.2616. The walk
        // reached back to 11:15, so only the 11:00 hour was seen whole-from-the-oldest-print-forward —
        // and an hour it did not reach is not reported, rather than reported as a zero it never
        // counted.
        var buckets = await Dydx().GetLiquidationVolumeAsync(
            "BTC-USD", DateTimeOffset.Parse("2026-09-13T09:00:00Z"), DateTimeOffset.Parse("2026-09-13T11:30:00Z"),
            CancellationToken.None);

        // The tape page is shorter than the page size, so the tape is exhausted: every hour from the
        // window's start was covered, and the two earlier hours are measured zeros.
        Assert.Equal(0.2616, buckets.Single(b => b.BucketTime == DateTimeOffset.Parse("2026-09-13T11:00:00Z")).Volume, 6);
        Assert.Equal(0d, buckets.Single(b => b.BucketTime == DateTimeOffset.Parse("2026-09-13T10:00:00Z")).Volume);
        Assert.All(buckets, b => Assert.Equal("base", b.VolumeUnit));
    }

    [Fact]
    public async Task The_side_on_the_tape_is_the_takers_and_the_venues_classification_is_kept()
    {
        var dydx = Dydx();
        await dydx.GetOrderBookAsync("BTC-USD", CancellationToken.None);

        var trades = dydx.DrainTrades();
        var liquidation = trades.Single(t => t.VenueUid == "06461c4b0000000200000002");

        Assert.Equal("sell", liquidation.TakerSide);

        // In the trade table's own vocabulary (0032). The venue's words — "limit", "liquidated" —
        // are not in its CHECK, and on the first live pass one such row failed every batch.
        Assert.Equal("liquidation", liquidation.TradeType);
        Assert.All(trades, t => Assert.Contains(t.TradeType, new[] { "fill", "liquidation", "termination" }));
        Assert.Equal(0.2613, liquidation.Qty, 6);
    }

    [Fact]
    public async Task A_minute_bar_carries_base_volume_quote_volume_and_its_count_of_prints()
    {
        var bars = await Dydx().GetCandles1mAsync(
            "BTC-USD", DateTimeOffset.Parse("2026-09-13T11:00:00Z"), DateTimeOffset.Parse("2026-09-13T11:30:00Z"),
            CancellationToken.None);

        var c = bars.Single(b => b.OpenTime == DateTimeOffset.Parse("2026-09-13T11:25:00Z"));
        Assert.Equal(76_653, c.Open, 6);
        Assert.Equal(0.0011, c.Volume, 6);
        Assert.Equal(84.3199, c.VolumeQuote!.Value, 4);
        Assert.Equal(3, c.TradeCount);
    }

    private sealed class Stub(List<string>? seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            seen?.Add(uri.ToString());
            var path = uri.AbsolutePath;
            var q = uri.Query;

            string? body = path switch
            {
                _ when path.EndsWith("/v4/perpetualMarkets", StringComparison.Ordinal) => Fixture("perpetualMarkets.json"),
                _ when path.Contains("/orderbooks/", StringComparison.Ordinal) => Fixture("orderbook_BTC-USD.json"),
                _ when path.Contains("/trades/", StringComparison.Ordinal) => Fixture("trades_BTC-USD.json"),
                _ when path.Contains("/candles/", StringComparison.Ordinal) && q.Contains("1HOUR", StringComparison.Ordinal) => Fixture("candles_1HOUR_BTC-USD.json"),
                _ when path.Contains("/candles/", StringComparison.Ordinal) => Fixture("candles_1MIN_BTC-USD.json"),
                _ when path.Contains("/historicalFunding/", StringComparison.Ordinal) => Fixture("historicalFunding_BTC-USD.json"),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

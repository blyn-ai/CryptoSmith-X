using System.Net;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Weex;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Drives the WEEX adapter against a stub handler that replays canonical WEEX Futures JSON —
/// captured from the live public API and trimmed — so the whole HTTP → JSON → mapping path runs with
/// no network. WEEX's REST is thinner than Kraken's: the batched ticker call carries no book size,
/// funding, or open interest, so most of these tests pin the merge-and-skip behaviour that fills the
/// gaps — a symbol missing any one of those must be omitted, never filled with a fabricated value.
/// </summary>
public sealed class WeexFuturesMarketDataTests
{
    private const string BaseUrl = "https://api-contract.weex.test";

    private static WeexFuturesMarketData Adapter(IWeexOpenInterestFeed? oi = null, FixtureHandler? handler = null) =>
        new(new WeexFuturesClient(new HttpClient(handler ?? new FixtureHandler()), BaseUrl),
            oi ?? new StubOpenInterestFeed(140557.0598, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));

    // ── Discovery ────────────────────────────────────────────────────────
    [Fact]
    public async Task Discovery_keeps_the_raw_multiplier_prefixed_base_and_converts_digit_counts_to_steps()
    {
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        // Every contract WEEX lists, including the dead tail — those are reported Halted rather
        // than hidden. See Discovery_reports_symbols_with_no_live_market_as_halted.
        Assert.Equal(
            new[] { "cmt_btcusdt", "cmt_ethusdt", "cmt_dogeusdt", "cmt_ltcusdt", "cmt_1000flokiusdt",
                    "cmt_gastownusdt", "cmt_usdcusdt" },
            instruments.Select(i => i.ExchangeSymbol).ToArray());

        var btc = instruments.Single(i => i.ExchangeSymbol == "cmt_btcusdt");
        Assert.Equal("BTC", btc.BaseAssetRaw);
        Assert.Equal("USDT", btc.QuoteAssetRaw);
        Assert.Equal(1m, btc.ContractMultiplier);        // sizes are already base-asset units on WEEX
        Assert.Equal(0.1m, btc.PriceStep);                // tick_size=1 is a DIGIT COUNT, not a step
        Assert.Equal(0.0001m, btc.QtyStep);               // size_increment=4
        Assert.Equal(0.0001m, btc.MinQty);
        Assert.Null(btc.MinNotional);                     // WEEX defines none
        Assert.Equal((short)8, btc.FundingIntervalHours); // collectCycle=480 min

        var doge = instruments.Single(i => i.ExchangeSymbol == "cmt_dogeusdt");
        Assert.Equal(0.00001m, doge.PriceStep);           // tick_size=5
        Assert.Equal(1m, doge.QtyStep);                   // size_increment=0 → whole contracts

        // 1000FLOKI: the raw base as WEEX spells it, NOT normalised to FLOKI here — that is the
        // alias table's job (0011 seeds the global 1000FLOKI→FLOKI×1000 alias).
        var floki = instruments.Single(i => i.ExchangeSymbol == "cmt_1000flokiusdt");
        Assert.Equal("1000FLOKI", floki.BaseAssetRaw);

        // Live contracts trade; the dead tail is Halted, not hidden — see the two status tests.
        Assert.All(
            instruments.Where(i => i.ExchangeSymbol is not ("cmt_gastownusdt" or "cmt_usdcusdt")),
            i => Assert.Equal(InstrumentStatus.Trading, i.Status));
        Assert.Contains("cmt_btcusdt", instruments.Single(i => i.ExchangeSymbol == "cmt_btcusdt").RawJson);
    }

    [Fact]
    public async Task Discovery_reports_symbols_with_no_live_market_as_halted()
    {
        // WEEX's /contracts lists a tail of abandoned symbols (ticker last=0) whose /candles and
        // /depth calls 400 rather than answering empty. They must stay out of those collectors —
        // but by status, not by being hidden: dropping them made discovery lose sight of the symbol
        // and invent a delisting three passes later, on a venue that never said any such thing.
        // Halted is what is actually observable: listed, not trading.
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal(
            InstrumentStatus.Halted,
            instruments.Single(i => i.ExchangeSymbol == "cmt_gastownusdt").Status);
    }

    [Fact]
    public async Task Discovery_halts_a_symbol_with_a_stale_price_but_zero_24h_volume()
    {
        // Found live: cmt_usdcusdt carries last=1.000581 (not zero) but volume_24h=0 — a stale
        // reference price on a contract with no real trades. Its /candles call still 400s, so a
        // price-only check would have missed exactly the case that matters.
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal(
            InstrumentStatus.Halted,
            instruments.Single(i => i.ExchangeSymbol == "cmt_usdcusdt").Status);
    }

    // ── Ticker merge: happy path + honest omission ──────────────────────
    [Fact]
    public async Task Tickers_merge_book_size_funding_and_cached_open_interest()
    {
        var oiAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var tickers = await Adapter(new StubOpenInterestFeed(140557.0598, oiAt)).GetTickersAsync(CancellationToken.None);

        var btc = tickers.Single(t => t.ExchangeSymbol == "cmt_btcusdt");
        Assert.Equal(78235.5, btc.LastPrice);
        Assert.Equal(78235.4, btc.BidPrice);              // from v2 tickers, not v3 bookTicker
        Assert.Equal(78235.5, btc.AskPrice);
        Assert.Equal(2.7548, btc.BidSize);                 // merged from v3 bookTicker (BTCUSDT)
        Assert.Equal(2.5631, btc.AskSize);
        Assert.Equal(0.00004120, btc.FundingRate, 10);     // already relative, used as-is
        Assert.Equal(1887684695.30752, btc.Turnover24h, 5);
        Assert.Equal(140557.0598, btc.OpenInterest);
        Assert.Equal(oiAt, btc.OpenInterestAt);            // OI's own, later time — not ReceivedAt
        Assert.Null(btc.Depth);

        // gastown: last/bid/ask are all "0" — WEEX's way of saying no market, not a real price.
        Assert.DoesNotContain(tickers, t => t.ExchangeSymbol == "cmt_gastownusdt");

        // LTC: a real, live-priced symbol the trimmed v3 bookTicker batch does not carry this round.
        // Skipped rather than shipped with a fabricated size — the honesty rule this adapter exists for.
        Assert.DoesNotContain(tickers, t => t.ExchangeSymbol == "cmt_ltcusdt");

        Assert.Equal(4, tickers.Count);   // btc, eth, doge, 1000floki — not ltc, not gastown
    }

    [Fact]
    public async Task A_symbol_missing_from_the_funding_batch_is_omitted_not_defaulted()
    {
        // A minimal, purpose-built response set: BTC everywhere except the funding batch.
        var handler = new FixtureHandler(fundingOverride: "[]");
        var tickers = await Adapter(handler: handler).GetTickersAsync(CancellationToken.None);
        Assert.DoesNotContain(tickers, t => t.ExchangeSymbol == "cmt_btcusdt");
    }

    [Fact]
    public async Task A_symbol_with_no_open_interest_sample_yet_is_omitted()
    {
        var tickers = await Adapter(new StubOpenInterestFeed(null, default)).GetTickersAsync(CancellationToken.None);
        Assert.Empty(tickers);   // every symbol in the fixture waits on the same never-sampled feed
    }

    // ── Candles ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Candles_are_sorted_ascending_drop_the_forming_bar_and_carry_no_trade_count()
    {
        // The fixture's 5 rows arrive out of chronological order; `to` sits exactly on the newest
        // bar's open time, so that bar is still forming and must be dropped.
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1788274680000);
        var from = DateTimeOffset.FromUnixTimeMilliseconds(1788274440000);

        var candles = await Adapter().GetCandles1mAsync("cmt_btcusdt", from, to, CancellationToken.None);

        Assert.Equal(4, candles.Count);
        Assert.All(candles, c => Assert.Null(c.TradeCount));
        for (var i = 1; i < candles.Count; i++)
        {
            Assert.Equal(TimeSpan.FromMinutes(1), candles[i].OpenTime - candles[i - 1].OpenTime);
        }

        var first = candles[0];
        Assert.Equal(78288.4, first.Open);
        Assert.Equal(11.6030, first.Volume, 4);   // index 5 (base units), not index 6 (quote value)
    }

    // ── Funding history ──────────────────────────────────────────────────
    [Fact]
    public async Task Funding_history_is_reversed_to_oldest_first_and_windowed()
    {
        // Fixture rows sit on 8h boundaries: 08-31 00:00/08:00/16:00, 09-01 00:00/08:00. This window
        // (edges inclusive) keeps the middle three and drops the oldest and the newest.
        var from = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var rates = await Adapter().GetFundingHistoryAsync("cmt_btcusdt", from, to, CancellationToken.None);

        Assert.Equal(3, rates.Count);
        for (var i = 1; i < rates.Count; i++)
        {
            Assert.True(rates[i].FundingTime > rates[i - 1].FundingTime);   // oldest first
        }
    }

    // ── Depth ────────────────────────────────────────────────────────────
    [Fact]
    public async Task A_shallow_real_book_leaves_every_band_null()
    {
        // WEEX's own default depth (limit=15) for BTC: real captured levels, but they span only a few
        // dollars around mid — nowhere near the ~$78 that 10 bps needs. Every band is honestly null.
        var depth = await Adapter(handler: new FixtureHandler(depthFile: "depth_shallow.json"))
            .GetOrderBookAsync("cmt_btcusdt", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.Null(depth!.Bid10Bps);
        Assert.Null(depth.Ask10Bps);
        Assert.Null(depth.Bid50Bps);
        Assert.Null(depth.Ask50Bps);
    }

    [Fact]
    public async Task A_symbol_with_no_book_returns_null_depth_not_an_exception()
    {
        // Found live: WEEX serves a symbol with no market as {"asks":null,"bids":null} — literal
        // JSON nulls, which override the DTO's [] default. This used to NullReferenceException.
        var depth = await Adapter(handler: new FixtureHandler(depthFile: "depth_null.json"))
            .GetOrderBookAsync("cmt_gastownusdt", CancellationToken.None);

        Assert.Null(depth);
    }

    [Fact]
    public async Task A_wider_real_book_sums_the_bands_it_reaches_and_nulls_the_rest()
    {
        // limit=200, trimmed to 60 levels/side — real captured book. Values below are computed
        // directly from that fixture (mid = 77893.25), not hand-derived.
        var depth = await Adapter().GetOrderBookAsync("cmt_btcusdt", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.Equal(20754083.9, depth!.Bid10Bps!.Value, 0);
        Assert.Equal(23141504.8, depth.Bid25Bps!.Value, 0);
        Assert.Null(depth.Bid50Bps);          // the trimmed 60-level fixture does not reach 50 bps
        Assert.Equal(49479991.48, depth.Ask10Bps!.Value, 1);
        Assert.Null(depth.Ask25Bps);          // the ask side is thinner in this fixture; 25 bps is unbounded
        Assert.Null(depth.Ask50Bps);
    }

    /// <summary>Replays a fixture per endpoint; routes v2's contracts/tickers/currentFundRate/
    /// candles/getHistoryFundRate/open_interest/depth and v3's bookTicker by path.</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "weex");

        private readonly string? _fundingOverride;
        private readonly string _depthFile;

        public FixtureHandler(string? fundingOverride = null, string depthFile = "depth.json")
        {
            _fundingOverride = fundingOverride;
            _depthFile = depthFile;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/capi/v2/market/contracts", StringComparison.Ordinal))
            {
                return Json("contracts.json");
            }

            if (path.EndsWith("/capi/v2/market/tickers", StringComparison.Ordinal))
            {
                return Json("tickers.json");
            }

            if (path.EndsWith("/capi/v3/market/ticker/bookTicker", StringComparison.Ordinal))
            {
                return Json("booktickers.json");
            }

            if (path.EndsWith("/capi/v2/market/currentFundRate", StringComparison.Ordinal))
            {
                return _fundingOverride is not null ? Raw(_fundingOverride) : Json("fundingrates.json");
            }

            if (path.EndsWith("/capi/v2/market/candles", StringComparison.Ordinal))
            {
                return Json("candles.json");
            }

            if (path.EndsWith("/capi/v2/market/getHistoryFundRate", StringComparison.Ordinal))
            {
                return Json("fundinghistory.json");
            }

            if (path.EndsWith("/capi/v2/market/open_interest", StringComparison.Ordinal))
            {
                return Json("openinterest.json");
            }

            if (path.EndsWith("/capi/v2/market/depth", StringComparison.Ordinal))
            {
                return Json(_depthFile);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string fixture) => Raw(File.ReadAllText(Path.Combine(Dir, fixture)));

        private static Task<HttpResponseMessage> Raw(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>A fixed answer for every symbol — enough to prove the merge logic without driving the
    /// real background cycle (that gets its own tests below).</summary>
    private sealed class StubOpenInterestFeed : IWeexOpenInterestFeed
    {
        private readonly double? _value;
        private readonly DateTimeOffset _at;

        public StubOpenInterestFeed(double? value, DateTimeOffset at)
        {
            _value = value;
            _at = at;
        }

        public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
        {
            if (_value is { } v)
            {
                openInterest = v;
                at = _at;
                return true;
            }

            openInterest = 0;
            at = default;
            return false;
        }
    }
}

using System.Net;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Hyperliquid;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Drives the Hyperliquid adapter against a stub handler that replays canonical Hyperliquid JSON —
/// captured from the live public <c>/info</c> endpoint and trimmed — so the whole HTTP → JSON →
/// mapping path runs with no network. Unlike Kraken/WEEX, Hyperliquid's batched ticker call carries no
/// book at all, so most of the ticker tests pin the merge-and-skip behaviour against a stub
/// <see cref="IHyperliquidLiveFeed"/> instead of a stub HTTP fixture.
/// </summary>
public sealed class HyperliquidMarketDataTests
{
    private const string BaseUrl = "https://api.hyperliquid.test";

    private static HyperliquidMarketData Adapter(
        IHyperliquidLiveFeed? restFeed = null, IHyperliquidLiveFeed? wsFeed = null, FixtureHandler? handler = null) =>
        new(new HyperliquidClient(new HttpClient(handler ?? new FixtureHandler()), BaseUrl),
            restFeed ?? StubLiveFeed.WithDefaults(), wsFeed);

    // ── Discovery ────────────────────────────────────────────────────────
    [Fact]
    public async Task Discovery_excludes_delisted_coins_and_converts_szDecimals_to_steps()
    {
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        Assert.Equal(new[] { "BTC", "ETH", "DOGE", "kPEPE" }, instruments.Select(i => i.ExchangeSymbol).ToArray());

        var btc = instruments.Single(i => i.ExchangeSymbol == "BTC");
        Assert.Equal("BTC", btc.BaseAssetRaw);
        Assert.Equal("USD", btc.QuoteAssetRaw);
        Assert.Equal(1m, btc.ContractMultiplier);
        Assert.Equal(0.1m, btc.PriceStep);      // 6 - szDecimals(5) = 1 decimal
        Assert.Equal(0.00001m, btc.QtyStep);    // szDecimals=5
        Assert.Equal(0.00001m, btc.MinQty);
        Assert.Null(btc.MinNotional);
        Assert.Equal((short)1, btc.FundingIntervalHours);
        Assert.Null(btc.ListedAt);
        Assert.Equal(InstrumentStatus.Trading, btc.Status);
        Assert.Contains("\"name\":\"BTC\"", btc.RawJson);

        var eth = instruments.Single(i => i.ExchangeSymbol == "ETH");
        Assert.Equal(0.01m, eth.PriceStep);     // 6 - szDecimals(4) = 2 decimals
        Assert.Equal(0.0001m, eth.QtyStep);

        var doge = instruments.Single(i => i.ExchangeSymbol == "DOGE");
        Assert.Equal(0.000001m, doge.PriceStep); // 6 - szDecimals(0) = 6 decimals
        Assert.Equal(1m, doge.QtyStep);          // whole units

        // kPEPE: the raw symbol as Hyperliquid spells it, NOT normalised here — that is the alias
        // table's job (0006 seeds the global kPEPE→PEPE×1000 alias; 0012 adds the other live k-coins).
        var kpepe = instruments.Single(i => i.ExchangeSymbol == "kPEPE");
        Assert.Equal("kPEPE", kpepe.BaseAssetRaw);

        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "MATIC");   // isDelisted: true
    }

    // ── Ticker merge: happy path + honest omission ─────────────────────
    [Fact]
    public async Task Tickers_merge_ctx_fields_with_book_top_from_the_live_feed()
    {
        var tickers = await Adapter().GetTickersAsync(CancellationToken.None);

        var btc = tickers.Single(t => t.ExchangeSymbol == "BTC");
        Assert.Equal(77269.5, btc.LastPrice);      // ctx.midPx — no separate last-trade field exists
        Assert.Equal(77200.0, btc.BidPrice);       // from the stub live feed, not the REST ctx call
        Assert.Equal(77250.0, btc.AskPrice);
        Assert.Equal(1.5, btc.BidSize);
        Assert.Equal(2.5, btc.AskSize);
        Assert.Equal(77269.7, btc.MarkPrice);
        Assert.Equal(77294.6, btc.IndexPrice);     // oraclePx stands in for index price
        Assert.Equal(0.0000125, btc.FundingRate);  // already a fraction of notional; no rescale
        Assert.Equal(2527349740.7496991158, btc.Turnover24h, 3);
        Assert.Equal(39580.19938, btc.OpenInterest);
        Assert.Equal(btc.ReceivedAt, btc.OpenInterestAt); // batched with the ticker call itself
        Assert.Null(btc.Depth);                    // depth is a separate call; see GetOrderBookAsync
    }

    [Fact]
    public async Task Tickers_omit_a_coin_with_no_fresh_book_sample_from_either_feed()
    {
        // The stub only knows BTC/ETH/DOGE; kPEPE has no top-of-book sample.
        var tickers = await Adapter(StubLiveFeed.WithDefaults()).GetTickersAsync(CancellationToken.None);
        Assert.DoesNotContain(tickers, t => t.ExchangeSymbol == "kPEPE");
    }

    [Fact]
    public async Task Tickers_prefer_the_ws_feed_over_the_rest_feed_when_both_have_a_sample()
    {
        var rest = StubLiveFeed.WithDefaults();
        var ws = new StubLiveFeed();
        ws.SetTop("BTC", new BookTop(BidPrice: 1, BidSize: 1, AskPrice: 2, AskSize: 1));

        var tickers = await Adapter(rest, ws).GetTickersAsync(CancellationToken.None);

        var btc = tickers.Single(t => t.ExchangeSymbol == "BTC");
        Assert.Equal(1, btc.BidPrice);   // the ws sample, not the rest feed's 77200
    }

    [Fact]
    public async Task Tickers_fall_back_to_the_rest_feed_when_the_ws_feed_has_no_sample_for_a_coin()
    {
        var rest = StubLiveFeed.WithDefaults();
        var ws = new StubLiveFeed();   // empty — every TryGetTop returns false

        var tickers = await Adapter(rest, ws).GetTickersAsync(CancellationToken.None);

        var btc = tickers.Single(t => t.ExchangeSymbol == "BTC");
        Assert.Equal(77200.0, btc.BidPrice);   // fell back to rest
    }

    // ── Candles ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Candles_sort_chronologically_and_carry_trade_count()
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(1788285180000);
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1788285420000 + 60_000);   // covers all 5 fixture bars
        var candles = await Adapter().GetCandles1mAsync("BTC", from, to, CancellationToken.None);

        Assert.Equal(5, candles.Count);
        Assert.Equal(candles.Select(c => c.OpenTime).OrderBy(t => t), candles.Select(c => c.OpenTime));

        var first = candles[0];
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1788285180000), first.OpenTime);
        Assert.Equal(77325.0, first.Open);
        Assert.Equal(77342.0, first.High);
        Assert.Equal(77323.0, first.Low);
        Assert.Equal(77332.0, first.Close);
        Assert.Equal(30.10522, first.Volume);
        Assert.Equal(223, first.TradeCount);
    }

    [Fact]
    public async Task Candles_drop_the_still_forming_bar()
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(1788285180000);
        // `to` lands inside the last fixture bar's minute — it must not be returned as closed.
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1788285420000 + 1000);
        var candles = await Adapter().GetCandles1mAsync("BTC", from, to, CancellationToken.None);

        Assert.DoesNotContain(candles, c => c.OpenTime == DateTimeOffset.FromUnixTimeMilliseconds(1788285420000));
    }

    // ── Funding history ──────────────────────────────────────────────────
    [Fact]
    public async Task FundingHistory_passes_through_sorted_by_time()
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(1788030000041);
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1788044400024);
        var rates = await Adapter().GetFundingHistoryAsync("BTC", from, to, CancellationToken.None);

        Assert.Equal(5, rates.Count);
        Assert.Equal(rates.Select(r => r.FundingTime).OrderBy(t => t), rates.Select(r => r.FundingTime));
        Assert.All(rates, r => Assert.Equal(0.0000125, r.Rate));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1788030000041), rates[0].FundingTime);
    }

    // ── Depth (GetOrderBookAsync) ────────────────────────────────────────
    [Fact]
    public async Task GetOrderBookAsync_reads_depth_from_the_ws_feed_first()
    {
        var restDepth = new Depth(1, 1, 1, 1, 1, 1, DateTimeOffset.UtcNow);
        var wsDepth = new Depth(2, 2, 2, 2, 2, 2, DateTimeOffset.UtcNow);
        var rest = new StubLiveFeed();
        rest.SetDepth("BTC", restDepth);
        var ws = new StubLiveFeed();
        ws.SetDepth("BTC", wsDepth);

        var depth = await Adapter(rest, ws).GetOrderBookAsync("BTC", CancellationToken.None);
        Assert.Equal(2, depth!.Bid10Bps);
    }

    [Fact]
    public async Task GetOrderBookAsync_returns_null_when_neither_feed_has_a_sample()
    {
        var depth = await Adapter(new StubLiveFeed(), new StubLiveFeed()).GetOrderBookAsync("BTC", CancellationToken.None);
        Assert.Null(depth);
    }

    /// <summary>Replays a fixture per request "type"; l2Book and candle/funding calls are routed by
    /// coin too, though only BTC is exercised by these adapter tests.</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyperliquid");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var type = doc.RootElement.GetProperty("type").GetString();

            return type switch
            {
                "meta" => Json("meta.json"),
                "metaAndAssetCtxs" => Json("meta_and_ctxs.json"),
                "candleSnapshot" => Json("candles_btc.json"),
                "fundingHistory" => Json("funding_history_btc.json"),
                "l2Book" => Json("l2book_BTC.json"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string fixture) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(Path.Combine(Dir, fixture)), System.Text.Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>A dictionary-backed live feed: set per-symbol answers, or use
    /// <see cref="WithDefaults"/> for a fixed BTC/ETH/DOGE top-of-book (no depth) — enough to prove the
    /// merge logic without driving the real background cycle (that gets its own coverage in
    /// <see cref="HyperliquidBookMathTests"/>).</summary>
    private sealed class StubLiveFeed : IHyperliquidLiveFeed
    {
        private readonly Dictionary<string, BookTop> _tops = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Depth> _depths = new(StringComparer.Ordinal);

        public static StubLiveFeed WithDefaults()
        {
            var feed = new StubLiveFeed();
            feed.SetTop("BTC", new BookTop(BidPrice: 77200.0, BidSize: 1.5, AskPrice: 77250.0, AskSize: 2.5));
            feed.SetTop("ETH", new BookTop(BidPrice: 2420.0, BidSize: 3, AskPrice: 2421.0, AskSize: 4));
            feed.SetTop("DOGE", new BookTop(BidPrice: 0.082, BidSize: 1000, AskPrice: 0.0821, AskSize: 2000));
            return feed;
        }

        public void SetTop(string symbol, BookTop top) => _tops[symbol] = top;

        public void SetDepth(string symbol, Depth depth) => _depths[symbol] = depth;

        public bool TryGetTop(string symbol, out BookTop top) => _tops.TryGetValue(symbol, out top!);

        public bool TryGetDepth(string symbol, out Depth depth) => _depths.TryGetValue(symbol, out depth!);
    }
}

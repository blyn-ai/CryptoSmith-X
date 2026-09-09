using System.Net;
using CryptoSmithX.MarketData.Connectors.Binance;
using CryptoSmithX.MarketData.Connectors.Market;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The adapter's WS-first, REST-fallback contract for the second Binance socket — ticker context and
/// candles — mirroring <see cref="KrakenWsFallbackTests"/> and <see cref="HyperliquidWsFallbackTests"/>:
/// a healthy feed's slice is served straight through, an unhealthy one (or none) falls through to
/// REST. Proven with a stub feed and the same stubbed HTTP handler <see cref="BinanceUsdmMarketDataTests"/>
/// uses, no socket.
/// </summary>
public sealed class BinanceMarketWsFallbackTests
{
    private const string BaseUrl = "https://fapi.binance.test";
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "binance");
    private static readonly DateTimeOffset At = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static BinanceUsdmMarketData Adapter(IBinanceMarketFeed marketFeed) =>
        new(new BinanceUsdmClient(new HttpClient(new RestHandler()), BaseUrl),
            openInterest: OpenInterest.ForEverything(),
            marketFeed: marketFeed,
            clock: new FakeTimeProvider(At));

    [Fact]
    public async Task Fresh_context_is_served_instead_of_the_two_extra_REST_calls()
    {
        // BTCUSDT is in bookticker.json (still fetched unconditionally — see IBinanceMarketFeed's
        // class remarks on why bid/ask has no WS source), but NOT in premiumindex.json/ticker24hr.json
        // by construction of this test's own context — proving those two calls were never made.
        var feed = new StubFeed
        {
            ContextsOk = true,
            Contexts = [new BinanceContext("BTCUSDT", LastPrice: 1, MarkPrice: 2, IndexPrice: 3, FundingRate: 4, Turnover24h: 5, At)],
        };

        var tickers = await Adapter(feed).GetTickersAsync(CancellationToken.None);

        var btc = Assert.Single(tickers);
        Assert.Equal("BTCUSDT", btc.ExchangeSymbol);
        Assert.Equal(1, btc.LastPrice);
        Assert.Equal(2, btc.MarkPrice);
        Assert.Equal(79919.20, btc.BidPrice);   // came from bookTicker REST, which stays unconditional
    }

    [Fact]
    public async Task A_context_with_no_book_ticker_row_is_omitted_not_half_written()
    {
        var feed = new StubFeed
        {
            ContextsOk = true,
            Contexts = [new BinanceContext("NOT_IN_BOOKTICKER", 1, 2, 3, 4, 5, At)],
        };

        var tickers = await Adapter(feed).GetTickersAsync(CancellationToken.None);

        Assert.Empty(tickers);
    }

    [Fact]
    public async Task Unhealthy_context_falls_back_to_the_full_REST_composition()
    {
        var feed = new StubFeed { ContextsOk = false };

        var tickers = await Adapter(feed).GetTickersAsync(CancellationToken.None);

        Assert.Contains(tickers, t => t.ExchangeSymbol == "BTCUSDT");   // came from premiumIndex + ticker/24hr
    }

    [Fact]
    public async Task Fresh_candles_are_served_instead_of_REST()
    {
        var wsBar = new Candle("BTCUSDT", At, 1, 1, 1, 1, 1, 1);
        var feed = new StubFeed { CandlesOk = true, Candles = [wsBar] };

        var candles = await Adapter(feed).GetCandles1mAsync("BTCUSDT", At, At.AddMinutes(1), CancellationToken.None);

        Assert.Same(wsBar, Assert.Single(candles));
    }

    [Fact]
    public async Task Incomplete_candle_range_falls_back_to_REST()
    {
        var feed = new StubFeed { CandlesOk = false };

        var candles = await Adapter(feed).GetCandles1mAsync("BTCUSDT", At, At.AddMinutes(5), CancellationToken.None);

        Assert.NotEmpty(candles);   // came from klines.json, not the (empty) WS stub
    }

    private sealed class StubFeed : IBinanceMarketFeed
    {
        public bool ContextsOk { get; init; }
        public IReadOnlyList<BinanceContext> Contexts { get; init; } = [];
        public bool CandlesOk { get; init; }
        public IReadOnlyList<Candle> Candles { get; init; } = [];

        public bool TryGetFreshContexts(out IReadOnlyList<BinanceContext> contexts)
        {
            contexts = Contexts;
            return ContextsOk;
        }

        public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
        {
            candles = Candles;
            return CandlesOk;
        }
    }

    private sealed class OpenInterest : IBinanceOpenInterestFeed
    {
        private readonly double _value;
        private readonly DateTimeOffset _at;

        private OpenInterest(double value, DateTimeOffset at)
        {
            _value = value;
            _at = at;
        }

        public static OpenInterest ForEverything() => new(106760.161, At);

        public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
        {
            openInterest = _value;
            at = _at;
            return true;
        }
    }

    private sealed class RestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var file = path switch
            {
                "/fapi/v1/ticker/bookTicker" => "bookticker.json",
                "/fapi/v1/premiumIndex" => "premiumindex.json",
                "/fapi/v1/ticker/24hr" => "ticker24hr.json",
                "/fapi/v1/klines" => "klines.json",
                _ => null,
            };

            if (file is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(File.ReadAllText(Path.Combine(Dir, file)), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

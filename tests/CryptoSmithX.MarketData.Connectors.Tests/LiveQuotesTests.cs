using System.Net;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Binance;
using CryptoSmithX.MarketData.Connectors.Hyperliquid;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Weex;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The live path's one hard rule, on all four venues: <see cref="IExchangeMarketData.LiveQuotes"/>
/// reads the socket's own caches and NOTHING else. No REST — not as a fallback, not to fill a gap,
/// not even once.
///
/// This is the rule that makes the whole design affordable. <c>GetTickersAsync</c> falls back to
/// REST when a feed is unhealthy, and that is right for a collector running every few seconds; the
/// same fallback on a 200 ms tick would be a request storm at the venue and a ban for us. So each
/// test below counts HTTP calls with a handler that fails any request outright, and a feed with
/// nothing in it must answer with an empty list rather than reaching for the wire.
/// </summary>
public sealed class LiveQuotesTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    /// <summary>Every symbol any stub below serves, so one set works for all four venues — the
    /// adapters filter to what they hold, which is the behaviour under test.</summary>
    private static readonly string[] Wanted = ["PF_XBTUSD", "BTC", "BTCUSDT", "cmt_btcusdt"];
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Kraken_with_an_empty_feed_answers_nothing_and_calls_no_REST()
    {
        var http = new CountingHandler();
        var adapter = new KrakenFuturesMarketData(KrakenClient(http), new KrakenStub { Fresh = false });

        Assert.Empty(adapter.LiveQuotes(Wanted, MaxAge));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Kraken_with_no_socket_at_all_answers_nothing_and_calls_no_REST()
    {
        // "This venue has no socket wired" is an answer, not a gap: the page keeps that row on the
        // database, which is exactly what a null figure means on the wire.
        var http = new CountingHandler();
        var adapter = new KrakenFuturesMarketData(KrakenClient(http), ws: null);

        Assert.Empty(adapter.LiveQuotes(Wanted, MaxAge));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Kraken_serves_the_whole_ticker_the_socket_holds()
    {
        // The one venue of the four whose socket carries a complete ticker, so nothing here is null
        // for want of a source — and At is the feed's receive time, never the poll's.
        var http = new CountingHandler();
        var feed = new KrakenStub { Fresh = true, Tickers = [Ticker("PF_XBTUSD", 100, 101)] };
        var adapter = new KrakenFuturesMarketData(KrakenClient(http), feed);

        var quote = Assert.Single(adapter.LiveQuotes(Wanted, MaxAge));

        Assert.Equal("PF_XBTUSD", quote.ExchangeSymbol);
        Assert.Equal(T0, quote.At);
        Assert.Equal(100, quote.BidPrice);
        Assert.Equal(101, quote.AskPrice);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Hyperliquid_with_an_empty_socket_answers_nothing_and_calls_no_REST()
    {
        var http = new CountingHandler();
        var adapter = new HyperliquidMarketData(
            new HyperliquidClient(new HttpClient(http), "https://api.hyperliquid.test"),
            new HyperliquidStub(),
            new HyperliquidStub { Fresh = false });

        Assert.Empty(adapter.LiveQuotes(Wanted, MaxAge));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Hyperliquid_never_reads_its_REST_baseline_feed()
    {
        // HyperliquidBookFeed is a REST poller wearing the same interface as the socket. The live
        // path must not touch it even when it is the only feed with data — that would be REST on a
        // 200 ms tick through a seam that happens to look local.
        var http = new CountingHandler();
        var restBaseline = new HyperliquidStub { Fresh = true, Contexts = [Context("BTC")] };
        var adapter = new HyperliquidMarketData(
            new HyperliquidClient(new HttpClient(http), "https://api.hyperliquid.test"),
            restBaseline,
            wsFeed: null);

        Assert.Empty(adapter.LiveQuotes(Wanted, MaxAge));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Hyperliquid_assembles_the_quote_from_context_and_book_top()
    {
        var http = new CountingHandler();
        var ws = new HyperliquidStub
        {
            Fresh = true,
            Contexts = [Context("BTC")],
            Top = new BookTop(100, 5, 101, 6),
        };
        var adapter = new HyperliquidMarketData(
            new HyperliquidClient(new HttpClient(http), "https://api.hyperliquid.test"),
            new HyperliquidStub(),
            ws);

        var quote = Assert.Single(adapter.LiveQuotes(Wanted, MaxAge));

        Assert.Equal(100, quote.BidPrice);
        Assert.Equal(6, quote.AskSize);
        Assert.Equal(70_000, quote.MarkPrice);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Binance_with_an_empty_market_feed_answers_nothing_and_calls_no_REST()
    {
        var http = new CountingHandler();

        Assert.Empty(Binance(http, new BinanceMarketStub { Fresh = false }, new BinanceDepthStub()).LiveQuotes(Wanted, MaxAge));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Binance_leaves_open_interest_null_because_no_socket_carries_it()
    {
        // OI on this venue is a background REST cycle, not a stream. Riding it under the word "live"
        // would put a REST cadence behind a 200 ms figure; null leaves that one cell on the database
        // and says so.
        var http = new CountingHandler();
        var market = new BinanceMarketStub { Fresh = true, Contexts = [BinanceContext("BTCUSDT")] };
        var depth = new BinanceDepthStub { Frame = Frame("BTCUSDT", 100, 101) };

        var quote = Assert.Single(Binance(http, market, depth).LiveQuotes(Wanted, MaxAge));

        Assert.Null(quote.OpenInterest);
        Assert.Equal(100, quote.BidPrice);
        Assert.Equal(101, quote.AskPrice);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Weex_with_no_socket_answers_nothing_and_calls_no_REST()
    {
        var http = new CountingHandler();
        var adapter = new WeexFuturesMarketData(WeexClient(http), new WeexOiStub(), ws: null);

        Assert.Empty(adapter.LiveQuotes(Wanted, MaxAge));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Weex_serves_the_book_top_and_nothing_it_has_no_channel_for()
    {
        // The thinnest live path of the four and honestly so: this socket has no ticker channel at
        // all, so mark, funding and open interest are null rather than filled from anywhere.
        var http = new CountingHandler();
        var ws = new WeexStub { Frame = Frame("cmt_btcusdt", 100, 101) };
        var adapter = new WeexFuturesMarketData(WeexClient(http), new WeexOiStub(), ws);

        var quote = Assert.Single(adapter.LiveQuotes(["cmt_btcusdt"], MaxAge));

        Assert.Equal(100, quote.BidPrice);
        Assert.Null(quote.MarkPrice);
        Assert.Null(quote.FundingRate);
        Assert.Null(quote.OpenInterest);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Weex_skips_a_subscribed_symbol_whose_book_has_not_seeded()
    {
        // Subscribed is not the same as holding a book: a symbol added seconds ago has no frame yet,
        // and an empty side is not a bid of zero.
        var http = new CountingHandler();
        var ws = new WeexStub { Frame = null };

        Assert.Empty(new WeexFuturesMarketData(WeexClient(http), new WeexOiStub(), ws).LiveQuotes(Wanted, MaxAge));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void An_adapter_assembles_only_the_symbols_it_was_asked_about()
    {
        // The guard on the mistake this seam shipped with. Assembling a quote means reading a book,
        // and reading a book means summing its levels — so answering for a whole venue put ~285 ms
        // of Kraken's 275 symbols into every 200 ms tick, for a page watching one listing. The feed
        // may hand back its whole slice; what must not happen is doing the per-symbol work for it.
        var http = new CountingHandler();
        var feed = new KrakenStub
        {
            Fresh = true,
            Tickers = [Ticker("PF_XBTUSD", 100, 101), Ticker("PF_ETHUSD", 2, 3), Ticker("PF_SOLUSD", 4, 5)],
        };

        var quotes = new KrakenFuturesMarketData(KrakenClient(http), feed).LiveQuotes(["PF_XBTUSD"], MaxAge);

        Assert.Equal(["PF_XBTUSD"], quotes.Select(q => q.ExchangeSymbol).ToArray());
        Assert.Equal(1, feed.DepthCalls);
    }

    [Fact]
    public void Weex_reads_its_book_only_for_the_symbols_wanted()
    {
        // WEEX is where this bit hardest: it subscribes depth for the venue's whole listing — about
        // a thousand books — so a whole-venue poll was a thousand cumulative-notional sums a tick.
        var http = new CountingHandler();
        var ws = new WeexStub { Frame = Frame("cmt_btcusdt", 100, 101) };

        new WeexFuturesMarketData(WeexClient(http), new WeexOiStub(), ws).LiveQuotes(["cmt_btcusdt"], MaxAge);

        Assert.Equal(["cmt_btcusdt"], ws.FrameCalls);
    }

    [Fact]
    public void Nothing_wanted_is_nothing_polled() =>
        Assert.Empty(new KrakenFuturesMarketData(
            KrakenClient(new CountingHandler()),
            new KrakenStub { Fresh = true, Tickers = [Ticker("PF_XBTUSD", 100, 101)] }).LiveQuotes([], MaxAge));

    [Fact]
    public void The_fake_adapter_has_no_live_path_at_all() =>
        // Through the interface, because LiveQuotes is a default member the fake does not override —
        // "this adapter has no socket" needs no code at all, which is the point of defaulting it.
        Assert.Empty(((IExchangeMarketData)new Fake.FakeExchangeMarketData()).LiveQuotes(Wanted, MaxAge));

    // ---------------------------------------------------------------------------------------

    private static KrakenFuturesClient KrakenClient(HttpMessageHandler http) =>
        new(new HttpClient(http), "https://futures.kraken.test", "https://futures.kraken.test/charts");

    private static WeexFuturesClient WeexClient(HttpMessageHandler http) =>
        new(new HttpClient(http), "https://weex.test");

    private static BinanceUsdmMarketData Binance(HttpMessageHandler http, IBinanceMarketFeed market, IBinanceLiveFeed depth) =>
        new(new BinanceUsdmClient(new HttpClient(http), "https://binance.test"),
            new BinanceOiStub(), depth, market, TimeProvider.System, NullLogger.Instance);

    private static Ticker Ticker(string symbol, double bid, double ask) =>
        new(symbol, T0, bid, bid, ask, 1, 1, bid, bid, 0.0001, 1_000, 10, T0, null);

    private static AssetContext Context(string symbol) =>
        new(symbol, 70_000, 70_000, 69_999, 0.0001, 1_000, 10, T0);

    private static BinanceContext BinanceContext(string symbol) =>
        new(symbol, 70_000, 70_000, 69_999, 0.0001, 1_000, T0);

    private static BookFrame Frame(string symbol, double bid, double ask) =>
        new(symbol, T0, 1, true, 1, [bid], [5], [ask], [6]);

    /// <summary>Fails every request rather than serving a fixture: a live path that reaches for REST
    /// should break the test loudly, not quietly succeed with borrowed data.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class KrakenStub : IKrakenLiveFeed
    {
        public bool Fresh { get; init; }
        public IReadOnlyList<Ticker> Tickers { get; init; } = [];
        public int DepthCalls { get; private set; }

        public bool TryGetFreshTickers(out IReadOnlyList<Ticker> tickers)
        {
            tickers = Tickers;
            return Fresh;
        }

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            DepthCalls++;
            depth = null!;
            return false;
        }
    }

    private sealed class HyperliquidStub : IHyperliquidLiveFeed
    {
        public bool Fresh { get; init; }
        public IReadOnlyList<AssetContext> Contexts { get; init; } = [];
        public BookTop? Top { get; init; }

        public bool TryGetTop(string symbol, out BookTop top)
        {
            top = Top!;
            return Top is not null;
        }

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            depth = null!;
            return false;
        }

        public bool TryGetFreshContexts(out IReadOnlyList<AssetContext> contexts)
        {
            contexts = Contexts;
            return Fresh;
        }

        public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
        {
            candles = [];
            return false;
        }
    }

    private sealed class BinanceMarketStub : IBinanceMarketFeed
    {
        public bool Fresh { get; init; }
        public IReadOnlyList<BinanceContext> Contexts { get; init; } = [];

        public bool TryGetFreshContexts(out IReadOnlyList<BinanceContext> contexts)
        {
            contexts = Contexts;
            return Fresh;
        }

        public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
        {
            candles = [];
            return false;
        }
    }

    private sealed class BinanceDepthStub : IBinanceLiveFeed
    {
        public BookFrame? Frame { get; init; }

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            depth = null!;
            return false;
        }

        public bool TryGetBookFrame(string symbol, int levels, out BookFrame frame)
        {
            frame = Frame!;
            return Frame is not null;
        }
    }

    private sealed class BinanceOiStub : IBinanceOpenInterestFeed
    {
        public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
        {
            openInterest = 0;
            at = default;
            return false;
        }
    }

    private sealed class WeexStub : IWeexLiveFeed
    {
        public BookFrame? Frame { get; init; }
        public List<string> FrameCalls { get; } = [];

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            depth = null!;
            return false;
        }

        public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
        {
            candles = [];
            return false;
        }

        public bool TryGetBookFrame(string symbol, int levels, out BookFrame frame)
        {
            FrameCalls.Add(symbol);
            frame = Frame!;
            return Frame is not null;
        }
    }

    private sealed class WeexOiStub : IWeexOpenInterestFeed
    {
        public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
        {
            openInterest = 0;
            at = default;
            return false;
        }
    }
}

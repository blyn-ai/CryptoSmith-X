using System.Net;
using CryptoSmithX.MarketData.Connectors.Hyperliquid;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The adapter's WS-first, REST-fallback contract for the two datasets this phase moved onto the
/// socket — ticker context and candles — mirroring <see cref="KrakenWsFallbackTests"/>: a healthy
/// feed's slice is served straight through, an unhealthy one (or none) falls through to REST. Proven
/// with a stub feed and a stubbed HTTP handler, no socket.
/// </summary>
public sealed class HyperliquidWsFallbackTests
{
    private const string BaseUrl = "https://api.hyperliquid.test";
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyperliquid");
    private static readonly DateTimeOffset At = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static HyperliquidMarketData Adapter(IHyperliquidLiveFeed feed) =>
        new(new HyperliquidClient(new HttpClient(new RestHandler()), BaseUrl), restFeed: feed, wsFeed: feed);

    [Fact]
    public async Task Fresh_context_is_served_instead_of_REST()
    {
        var feed = new StubFeed
        {
            ContextsFresh = true,
            Contexts = [new AssetContext("WS_ONLY", 1, 1, 1, 1, 1, 1, At)],
            Top = new BookTop(1, 1, 1, 1),
        };

        var tickers = await Adapter(feed).GetTickersAsync(CancellationToken.None);

        // Exactly the WS cache's own symbol, not the REST fixture's BTC/ETH/DOGE — proves the branch
        // never touches metaAndAssetCtxs at all when the context cache is healthy.
        Assert.Equal(["WS_ONLY"], tickers.Select(t => t.ExchangeSymbol).ToArray());
        Assert.Equal(At, tickers[0].ReceivedAt);
    }

    [Fact]
    public async Task A_context_with_no_fresh_book_sample_is_omitted_not_half_written()
    {
        var feed = new StubFeed
        {
            ContextsFresh = true,
            Contexts = [new AssetContext("NO_BOOK", 1, 1, 1, 1, 1, 1, At)],
            Top = null,   // book not fresh yet for this coin
        };

        var tickers = await Adapter(feed).GetTickersAsync(CancellationToken.None);

        Assert.Empty(tickers);
    }

    [Fact]
    public async Task Unhealthy_context_falls_back_to_REST()
    {
        // Top set for every coin the REST fixture lists, so the assertion below is about the
        // context source, not incidentally about the book merge this test is not exercising.
        var feed = new StubFeed { ContextsFresh = false, Top = new BookTop(1, 1, 1, 1) };

        var tickers = await Adapter(feed).GetTickersAsync(CancellationToken.None);

        Assert.Contains(tickers, t => t.ExchangeSymbol == "BTC");   // came from REST metaAndAssetCtxs
    }

    [Fact]
    public async Task Fresh_candles_are_served_instead_of_REST()
    {
        var from = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(1);
        var wsBar = new Candle("BTC", from, 1, 1, 1, 1, 1, 1);
        var feed = new StubFeed { CandlesOk = true, Candles = [wsBar] };

        var candles = await Adapter(feed).GetCandles1mAsync("BTC", from, to, CancellationToken.None);

        Assert.Same(wsBar, Assert.Single(candles));
    }

    [Fact]
    public async Task Incomplete_candle_range_falls_back_to_REST()
    {
        var from = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var feed = new StubFeed { CandlesOk = false };

        var candles = await Adapter(feed).GetCandles1mAsync("BTC", from, from.AddMinutes(5), CancellationToken.None);

        // Came from candles_btc.json, not the (empty) WS stub.
        Assert.NotEmpty(candles);
    }

    private sealed class StubFeed : IHyperliquidLiveFeed
    {
        public bool ContextsFresh { get; init; }
        public IReadOnlyList<AssetContext> Contexts { get; init; } = [];
        public BookTop? Top { get; init; }
        public bool CandlesOk { get; init; }
        public IReadOnlyList<Candle> Candles { get; init; } = [];

        public bool TryGetTop(string symbol, out BookTop top)
        {
            if (Top is { } t)
            {
                top = t;
                return true;
            }

            top = default!;
            return false;
        }

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            depth = default!;
            return false;
        }

        public bool TryGetFreshContexts(out IReadOnlyList<AssetContext> contexts)
        {
            contexts = Contexts;
            return ContextsFresh;
        }

        public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
        {
            candles = Candles;
            return CandlesOk;
        }
    }

    private sealed class RestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var file = body.Contains("\"metaAndAssetCtxs\"", StringComparison.Ordinal) ? "meta_and_ctxs.json"
                : body.Contains("\"candleSnapshot\"", StringComparison.Ordinal) ? "candles_btc.json"
                : null;
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

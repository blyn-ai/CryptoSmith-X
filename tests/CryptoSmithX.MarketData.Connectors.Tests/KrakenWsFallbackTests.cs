using System.Net;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The adapter's WS-first, REST-fallback contract: a healthy feed's slice is served straight through;
/// an unhealthy feed (or none) falls through to the REST call. Proven with a stub feed and a stubbed
/// HTTP handler — no socket, no network.
/// </summary>
public sealed class KrakenWsFallbackTests
{
    private static KrakenFuturesClient Client() =>
        new(new HttpClient(new RestHandler()), "https://futures.kraken.test", "https://futures.kraken.test/api/charts/v1");

    [Fact]
    public async Task Fresh_feed_is_served_instead_of_REST()
    {
        var feed = new StubFeed { TickersFresh = true, Tickers = [Sample("WS_ONLY")] };
        var adapter = new KrakenFuturesMarketData(Client(), feed);

        var tickers = await adapter.GetTickersAsync(CancellationToken.None);

        Assert.Equal(["WS_ONLY"], tickers.Select(t => t.ExchangeSymbol).ToArray());   // the cache slice, not REST
    }

    [Fact]
    public async Task Unhealthy_feed_falls_back_to_REST()
    {
        var feed = new StubFeed { TickersFresh = false };
        var adapter = new KrakenFuturesMarketData(Client(), feed);

        var tickers = await adapter.GetTickersAsync(CancellationToken.None);

        Assert.Contains(tickers, t => t.ExchangeSymbol == "PF_XBTUSD");   // came from REST /tickers
    }

    [Fact]
    public async Task No_feed_uses_REST()
    {
        var adapter = new KrakenFuturesMarketData(Client(), ws: null);
        var tickers = await adapter.GetTickersAsync(CancellationToken.None);
        Assert.Contains(tickers, t => t.ExchangeSymbol == "PF_XBTUSD");
    }

    [Fact]
    public async Task Depth_prefers_the_live_book_then_REST()
    {
        var at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var wsDepth = new Depth(1, 1, 1, 1, 1, 1, at);
        var live = new StubFeed { DepthFresh = true, DepthValue = wsDepth };
        Assert.Equal(at, (await new KrakenFuturesMarketData(Client(), live).GetOrderBookAsync("PF_XBTUSD", CancellationToken.None))!.At);

        var stale = new StubFeed { DepthFresh = false };
        var rest = await new KrakenFuturesMarketData(Client(), stale).GetOrderBookAsync("PF_XBTUSD", CancellationToken.None);
        Assert.NotNull(rest);
        Assert.NotEqual(at, rest!.At);   // came from REST /orderbook, not the stub
    }

    private static Ticker Sample(string symbol) =>
        new(symbol, DateTimeOffset.UnixEpoch, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, DateTimeOffset.UnixEpoch, null);

    private sealed class StubFeed : IKrakenLiveFeed
    {
        public bool TickersFresh { get; init; }
        public IReadOnlyList<Ticker> Tickers { get; init; } = [];
        public bool DepthFresh { get; init; }
        public Depth? DepthValue { get; init; }

        public bool TryGetFreshTickers(out IReadOnlyList<Ticker> tickers)
        {
            tickers = Tickers;
            return TickersFresh;
        }

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            depth = DepthValue!;
            return DepthFresh;
        }
    }

    private sealed class RestHandler : HttpMessageHandler
    {
        private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kraken");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var file = path.EndsWith("/tickers", StringComparison.Ordinal) ? "tickers.json"
                : path.EndsWith("/orderbook", StringComparison.Ordinal) ? "orderbook.json"
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

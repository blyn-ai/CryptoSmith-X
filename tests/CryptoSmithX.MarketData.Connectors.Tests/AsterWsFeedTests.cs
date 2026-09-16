using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Binance;
using CryptoSmithX.MarketData.Connectors.Pacing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Two concerns that only matter once a venue's collected set can plausibly approach the
/// per-connection stream cap — Aster's 200 against Binance's 1024 — plus the WS protocol facts
/// Fixtures/aster-ws pins from a live capture, mirroring <see cref="BinanceWsProtocolTests"/>.
/// </summary>
public sealed class AsterWsFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "aster-ws", name);

    // ── The stream-cap guard ─────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Depth_feed_caps_at_the_profiles_stream_limit_and_keeps_the_first_symbols_in_order()
    {
        // 250 synthetic symbols, well past Aster's 200-stream depth cap and far past the 26 the
        // approved universe (blueprint §6) actually collects — the guard has to hold even if the
        // collected set grows past what was planned.
        var wanted = Enumerable.Range(0, 250).Select(i => $"SYM{i:D4}USDT").ToArray();
        var clock = new FakeTimeProvider(T0);
        var feed = new BinanceWsFeed(
            "ws://localhost:1/", new BinanceUsdmClient("http://localhost:1/"),
            new VenueGate("ASTER-TEST", 5, 4, clock), NullLoggerFactory.Instance, clock,
            staleAfter: TimeSpan.FromSeconds(30), crosscheckInterval: TimeSpan.FromMinutes(5), driftBps: 50,
            profile: BinanceUsdmProfile.Aster,
            collectedSymbolsAsync: _ => Task.FromResult(wanted));

        // _conn is not connected (Start() was never called; there is no socket in this test), so
        // RefreshSymbolsAsync computes the capped set and returns before trying to subscribe —
        // exactly the shape that makes this callable directly, no socket needed.
        await feed.RefreshSymbolsAsync(CancellationToken.None);

        Assert.Equal(200, feed.SubscribedSymbols.Length);
        var ordered = wanted.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(ordered[..200], feed.SubscribedSymbols);
    }

    [Fact]
    public async Task Depth_feed_under_the_binance_profile_never_caps_the_whole_venue()
    {
        // The regression this guard must not cause: Binance's ~570 in-scope symbols must come back
        // whole — its profile carries no cap. WholeVenue mode reads from the client
        // (GetSymbolsAsync), so this uses a stub handler the way BinanceUsdmMarketDataTests does.
        var clock = new FakeTimeProvider(T0);
        var handler = new SingleSymbolExchangeInfoHandler(count: 570);
        var feed = new BinanceWsFeed(
            "ws://localhost:1/", new BinanceUsdmClient(new HttpClient(handler), "https://fapi.binance.test"),
            new VenueGate("BINANCE-TEST", 5, 4, clock), NullLoggerFactory.Instance, clock,
            staleAfter: TimeSpan.FromSeconds(30), crosscheckInterval: TimeSpan.FromMinutes(5), driftBps: 50,
            // Whole-venue on purpose: the uncapped path a large listing takes. Binance itself
            // subscribes its collected set since 2026-09-17; the no-cap property still matters for it.
            profile: BinanceUsdmProfile.Binance with { FeedSymbols = FeedSymbolsMode.WholeVenue });

        await feed.RefreshSymbolsAsync(CancellationToken.None);

        Assert.Equal(570, feed.SubscribedSymbols.Length);
    }

    [Fact]
    public async Task Depth_feed_waits_for_a_collected_set_before_connecting()
    {
        // Aster's first enable: the feed started before discovery had marked anything collected,
        // opened a socket with nothing to subscribe, and the idle watchdog cycled it eight times.
        // Now it polls the collected set and only takes the symbols once there are some.
        var clock = new FakeTimeProvider(T0);
        var calls = 0;
        string[] answer = [];
        using var cts = new CancellationTokenSource();
        var feed = new BinanceWsFeed(
            "ws://localhost:1/", new BinanceUsdmClient("http://localhost:1/"),
            new VenueGate("ASTER-TEST", 5, 4, clock), NullLoggerFactory.Instance, clock,
            staleAfter: TimeSpan.FromSeconds(30), crosscheckInterval: TimeSpan.FromMinutes(5), driftBps: 50,
            profile: BinanceUsdmProfile.Aster,
            collectedSymbolsAsync: _ => { Interlocked.Increment(ref calls); return Task.FromResult(answer); });

        feed.Start(cts.Token);
        await WaitUntil(() => calls >= 1);
        Assert.Empty(feed.SubscribedSymbols);

        answer = ["BTCUSDT", "ETHUSDT"];
        clock.Advance(TimeSpan.FromSeconds(31));
        await WaitUntil(() => feed.SubscribedSymbols.Length == 2);

        Assert.Equal(["BTCUSDT", "ETHUSDT"], feed.SubscribedSymbols);
        await cts.CancelAsync();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Market_feed_under_the_binance_profile_never_caps_the_whole_venue()
    {
        // The regression a 1024 cap would cause: 3 + 2 x 566 = 1 135 streams on Binance's market
        // feed, which it has always subscribed. With the cap applied, the ~56 alphabetically-last
        // symbols (XRP, XLM, WLD, ZEC among the collected) would lose WS trades and candles.
        var clock = new FakeTimeProvider(T0);
        var handler = new SingleSymbolExchangeInfoHandler(count: 570);
        var feed = new BinanceMarketWsFeed(
            "ws://localhost:1/", new BinanceUsdmClient(new HttpClient(handler), "https://fapi.binance.test"),
            NullLoggerFactory.Instance, clock,
            profile: BinanceUsdmProfile.Binance with { FeedSymbols = FeedSymbolsMode.WholeVenue });

        await feed.RefreshSymbolsAsync(CancellationToken.None);

        Assert.Equal(570, feed.SubscribedSymbols.Length);
    }

    [Fact]
    public async Task Market_feed_caps_symbols_so_fixed_plus_2N_streams_stay_under_the_limit()
    {
        // Market feed streams = 3 (the array streams) + 2N (kline_1m, aggTrade per symbol). At
        // Aster's cap of 200, N tops out at (200-3)/2 = 98 — the exact number blueprint §4.2 names
        // as "the named next step if the collected set passes 98 symbols".
        var wanted = Enumerable.Range(0, 150).Select(i => $"SYM{i:D4}USDT").ToArray();
        var clock = new FakeTimeProvider(T0);
        var feed = new BinanceMarketWsFeed(
            "ws://localhost:1/", new BinanceUsdmClient("http://localhost:1/"), NullLoggerFactory.Instance, clock,
            profile: BinanceUsdmProfile.Aster,
            collectedSymbolsAsync: _ => Task.FromResult(wanted));

        await feed.RefreshSymbolsAsync(CancellationToken.None);

        Assert.Equal(98, feed.SubscribedSymbols.Length);
    }

    [Fact]
    public async Task Market_feed_at_the_approved_26_symbol_universe_is_nowhere_near_the_cap()
    {
        // The universe blueprint §6 actually proposes: 25 auto-collect bases plus ASTERUSDT = 26.
        // Streams = 3 + 2*26 = 55, far under 200 — the guard exists for safety, not because this
        // universe needs it.
        var wanted = Enumerable.Range(0, 26).Select(i => $"SYM{i:D2}USDT").ToArray();
        var clock = new FakeTimeProvider(T0);
        var feed = new BinanceMarketWsFeed(
            "ws://localhost:1/", new BinanceUsdmClient("http://localhost:1/"), NullLoggerFactory.Instance, clock,
            profile: BinanceUsdmProfile.Aster,
            collectedSymbolsAsync: _ => Task.FromResult(wanted));

        await feed.RefreshSymbolsAsync(CancellationToken.None);

        Assert.Equal(26, feed.SubscribedSymbols.Length);
    }

    /// <summary>Replays a same-shaped exchangeInfo for N distinct, in-scope Binance perpetuals — only
    /// what <see cref="BinanceWsFeed.RefreshSymbolsAsync"/>'s WholeVenue branch reads.</summary>
    private sealed class SingleSymbolExchangeInfoHandler : HttpMessageHandler
    {
        private readonly int _count;

        public SingleSymbolExchangeInfoHandler(int count) => _count = count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var symbols = Enumerable.Range(0, _count).Select(i => $$"""
                {"symbol":"SYM{{i:D4}}USDT","contractType":"PERPETUAL","status":"TRADING","baseAsset":"SYM{{i:D4}}","quoteAsset":"USDT","onboardDate":1700000000000,"filters":[]}
                """);
            var body = "{\"symbols\":[" + string.Join(',', symbols) + "]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // ── Protocol facts, pinned to the live capture ──────────────────────────────────────────
    [Fact]
    public void The_captured_depth_run_has_the_same_seam_shape_binance_does()
    {
        // Same structure BinanceWsProtocolTests pins for Binance's own capture — reproduced here on
        // an independent venue, on the SAME wire shape (BinanceWsDepth, BinanceJson.Options), to
        // prove the "protocol: confirmed clone" verdict (blueprint §0/§1.6) on real bytes rather
        // than by assertion.
        var snapshot = JsonDocument.Parse(File.ReadAllText(FixturePath("depth-snapshot.json"))).RootElement;
        var lastUpdateId = snapshot.GetProperty("lastUpdateId").GetInt64();

        var lines = File.ReadAllLines(FixturePath("depth-deltas.jsonl")).Where(l => l.Length > 0).ToList();
        var deltas = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();

        var stale = deltas.Count(d => d.GetProperty("u").GetInt64() < lastUpdateId);
        Assert.Equal(8, stale);   // matches the live capture's own session-transcript.txt

        var seam = deltas.First(d => d.GetProperty("u").GetInt64() >= lastUpdateId);
        var u = seam.GetProperty("u").GetInt64();
        var first = seam.GetProperty("U").GetInt64();
        var previous = seam.GetProperty("pu").GetInt64();
        Assert.True(first <= lastUpdateId && lastUpdateId <= u, "the USDⓈ-M first-event rule holds on Aster too");
        Assert.NotEqual(lastUpdateId, previous);

        // Steady-state chaining across the whole captured run — the "seam" is about where the REST
        // snapshot's lastUpdateId happens to fall inside a continuous stream, not an actual break in
        // it, so the WS delta chain itself holds with zero exceptions end to end. Exactly the fact
        // BinanceWsProtocolTests pins for Binance's own capture (60 frames, 59 pairs, none).
        var breaks = 0;
        for (var i = 1; i < deltas.Count; i++)
        {
            if (deltas[i].GetProperty("pu").GetInt64() != deltas[i - 1].GetProperty("u").GetInt64())
            {
                breaks++;
            }
        }

        Assert.Equal(0, breaks);
    }

    [Fact]
    public void A_captured_depth_frame_binds_under_this_connectors_options()
    {
        // Each line is the bare depthUpdate object, not the {"stream":...,"data":{...}} envelope —
        // same convention Fixtures/binance-ws/depth-deltas.jsonl uses.
        var line = File.ReadAllLines(FixturePath("depth-deltas.jsonl")).First(l => l.Length > 0);
        var frame = JsonDocument.Parse(line).RootElement.Deserialize<BinanceWsDepth>(BinanceJson.Options);

        Assert.NotNull(frame);
        Assert.Equal("depthUpdate", frame!.EventType);
        Assert.Equal("BTCUSDT", frame.Symbol);
        Assert.True(frame.LastUpdateId >= frame.FirstUpdateId);
    }

    [Fact]
    public void The_ticker_array_pushes_changed_symbols_only_not_the_whole_venue()
    {
        // The fact BinanceUsdmProfile.Aster.TickersFromMarketFeed = false exists because of
        // (blueprint §1.4): a live capture, and it is nowhere near ~600.
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath("ticker-arr.json")));
        var data = doc.RootElement.GetProperty("data");

        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        var count = data.GetArrayLength();
        Assert.True(count > 0);
        Assert.True(count < 50, $"expected a change-only frame (few symbols), got {count}");
    }

    [Fact]
    public void The_markPrice_array_pushes_every_symbol_every_second()
    {
        // The contrast with the ticker array above: mark/index/funding is whole-venue every push —
        // blueprint §1.4 measured 746. Not re-asserted at that exact number (the venue's listing
        // moves), only that it is an order of magnitude larger than the change-only ticker frame.
        using var ticker = JsonDocument.Parse(File.ReadAllText(FixturePath("ticker-arr.json")));
        using var mark = JsonDocument.Parse(File.ReadAllText(FixturePath("markprice-arr.json")));

        var tickerCount = ticker.RootElement.GetProperty("data").GetArrayLength();
        var markCount = mark.RootElement.GetProperty("data").GetArrayLength();

        Assert.True(markCount > tickerCount * 10, $"expected markPrice ({markCount}) >> ticker ({tickerCount})");
        Assert.True(markCount > 500);
    }

    [Fact]
    public void The_forceOrder_event_is_the_same_shape_binances_handler_already_reads()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath("forceorder.json")));
        var root = doc.RootElement;
        Assert.Equal("!forceOrder@arr", root.GetProperty("stream").GetString());

        var o = root.GetProperty("data").GetProperty("o");
        Assert.True(o.TryGetProperty("s", out _));    // symbol
        Assert.True(o.TryGetProperty("S", out _));    // side
        Assert.True(o.TryGetProperty("z", out _));    // accumulated filled quantity
        Assert.True(o.TryGetProperty("ap", out _));   // average price
        Assert.True(o.TryGetProperty("T", out _));    // trade time
    }
}

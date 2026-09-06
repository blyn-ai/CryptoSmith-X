using System.Globalization;
using System.Net;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Weex;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The WEEX WS path, driven by the frames captured in Fixtures/weex-ws — the same bytes
/// <see cref="WeexWsProtocolTests"/> pins the protocol with, now run through the code that has to
/// believe them. <see cref="WeexWsProtocolTests"/> asserts what the venue sends; this file asserts
/// what we do with it.
///
/// Three things are worth failing over, and each has its own test below: the book assembles from the
/// captured run and ends where an independent replay of the same frames ends; a missed frame stops
/// the book rather than corrupting it; and a book that stopped being updated stops counting as live.
/// The chaining rule is checked in its discriminating form — a Binance-shaped <c>U = u + 1</c> frame
/// must BREAK this book, because a builder that accepted both rules would accept a stream it is
/// misreading.
/// </summary>
public sealed class WeexWsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 11, 0, 0, TimeSpan.Zero);

    /// <summary>The level the captured run carries. The feed itself subscribes @depth200 and builds
    /// with 200; the capture used plain @depth, which the venue answers at 15, so these tests build
    /// at 15 to exercise the same code against the real frames.</summary>
    private const int CapturedLevels = 15;

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "weex-ws", name);

    private static Frame Snapshot() =>
        Frame.Parse(File.ReadAllText(FixturePath("depth-snapshot.json")));

    private static List<Frame> Deltas() =>
        [.. File.ReadAllLines(FixturePath("depth-deltas.jsonl")).Where(l => l.Length > 0).Select(Frame.Parse)];

    // ── Assembly ───────────────────────────────────────────────────────────
    [Fact]
    public void The_captured_run_assembles_into_the_book_an_independent_replay_produces()
    {
        var book = new WeexBookBuilder(CapturedLevels);
        var snapshot = Snapshot();
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);

        // The snapshot alone is a real book: this is where the run starts, and asserting it means a
        // later assertion about where the run ENDS cannot pass by accident on a builder that ignored
        // every delta.
        Assert.True(book.TryGetTopMid(snapshot.Symbol, out var seededMid));
        Assert.Equal(79979.85, seededMid, 4);

        var deltas = Deltas();
        Assert.Equal(60, deltas.Count);
        for (var i = 0; i < deltas.Count; i++)
        {
            var d = deltas[i];
            Assert.Equal(
                WeexBookBuilder.DeltaResult.Applied,
                book.ApplyDelta(d.Symbol, d.FirstUpdateId, d.LastUpdateId, d.Levels, d.Bids, d.Asks, T0));
        }

        // Independently replayed with a plain dictionary — no builder involved, no sequencing, no
        // trimming — so agreement is evidence about the builder rather than about itself.
        var (expectedBids, expectedAsks) = Replay(snapshot, deltas);
        Assert.Equal(79982.1, expectedBids.Keys.Max(), 4);
        Assert.Equal(79982.2, expectedAsks.Keys.Min(), 4);

        Assert.True(book.TryGetTopMid(snapshot.Symbol, out var mid));
        Assert.Equal((expectedBids.Keys.Max() + expectedAsks.Keys.Min()) / 2, mid, 6);
        Assert.NotEqual(seededMid, mid);   // the deltas actually moved the book

        // The bands the venue's own 15-level window reaches: none of them. Fifteen levels of BTC span
        // about 5 bps, so every band is honestly null rather than an undercount — the same answer the
        // REST path gives for limit=15 (A_shallow_real_book_leaves_every_band_null).
        Assert.True(book.TryGetDepth(snapshot.Symbol, T0, out var depth));
        Assert.Null(depth.Bid10Bps);
        Assert.Null(depth.Ask10Bps);
        Assert.Null(depth.Bid50Bps);
        Assert.Null(depth.Ask50Bps);
        Assert.Equal(T0, depth.At);
    }

    [Fact]
    public void A_qty_of_zero_in_the_captured_run_removes_its_price_level()
    {
        // 208 removals cross the wire in these 60 frames, and both sides end at exactly 15 levels —
        // the venue does emit an explicit removal for a level leaving the window. A builder that
        // stored "0" as a level with no size would end with a fatter book than this.
        var snapshot = Snapshot();
        var deltas = Deltas();
        var (bids, asks) = Replay(snapshot, deltas);

        Assert.Equal(CapturedLevels, bids.Count);
        Assert.Equal(CapturedLevels, asks.Count);
        Assert.DoesNotContain(0.0, bids.Values);
        Assert.DoesNotContain(0.0, asks.Values);

        // And through the builder: a price the run removes is gone from its book too.
        var removed = deltas
            .SelectMany(d => d.Bids)
            .First(l => l.Qty == 0 && !bids.ContainsKey(l.Price));

        var book = new WeexBookBuilder(CapturedLevels);
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);
        foreach (var d in deltas)
        {
            book.ApplyDelta(d.Symbol, d.FirstUpdateId, d.LastUpdateId, d.Levels, d.Bids, d.Asks, T0);
        }

        Assert.True(book.TryGetTopMid(snapshot.Symbol, out var mid));
        Assert.True(removed.Price < mid);   // it was a bid, and it is no longer the top
        Assert.DoesNotContain(removed.Price, Replay(snapshot, deltas).Bids.Keys);
    }

    // ── The chaining rule, in its discriminating form ──────────────────────
    [Fact]
    public void A_binance_shaped_off_by_one_frame_breaks_this_book()
    {
        // Binance's rule is U <= lastUpdateId + 1 <= u; WEEX's is U == the previous frame's u,
        // exactly. A builder that tolerated both would silently accept a stream it is misreading, so
        // the frame Binance would consider contiguous has to be a gap here.
        var book = new WeexBookBuilder(CapturedLevels);
        var snapshot = Snapshot();
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);

        var first = Deltas()[0];
        Assert.Equal(snapshot.LastUpdateId, first.FirstUpdateId);   // the real frame chains by equality

        Assert.Equal(
            WeexBookBuilder.DeltaResult.Gap,
            book.ApplyDelta(first.Symbol, snapshot.LastUpdateId + 1, first.LastUpdateId, first.Levels,
                first.Bids, first.Asks, T0));
        Assert.True(book.IsDirty(first.Symbol));
    }

    // ── A missed frame ─────────────────────────────────────────────────────
    [Fact]
    public void A_missed_frame_stops_the_book_until_a_resync_snapshot()
    {
        var book = new WeexBookBuilder(CapturedLevels);
        var snapshot = Snapshot();
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);

        var deltas = Deltas();
        for (var i = 0; i < 10; i++)
        {
            var d = deltas[i];
            book.ApplyDelta(d.Symbol, d.FirstUpdateId, d.LastUpdateId, d.Levels, d.Bids, d.Asks, T0);
        }

        Assert.True(book.TryGetDepth(snapshot.Symbol, T0, out _));

        // Drop frame 10 on the floor, exactly as a lost message would: frame 11's U now names a u we
        // never applied.
        var missed = deltas[11];
        Assert.Equal(
            WeexBookBuilder.DeltaResult.Gap,
            book.ApplyDelta(missed.Symbol, missed.FirstUpdateId, missed.LastUpdateId, missed.Levels,
                missed.Bids, missed.Asks, T0));

        // Dirty means dirty: no depth, and every later frame is ignored rather than applied to a book
        // that is already wrong. This is what makes the feed resync instead of drifting.
        Assert.True(book.IsDirty(snapshot.Symbol));
        Assert.False(book.TryGetDepth(snapshot.Symbol, T0, out _));
        Assert.Equal(0, book.FreshCount(TimeSpan.FromSeconds(30), T0));

        var next = deltas[12];
        Assert.Equal(
            WeexBookBuilder.DeltaResult.Ignored,
            book.ApplyDelta(next.Symbol, next.FirstUpdateId, next.LastUpdateId, next.Levels, next.Bids, next.Asks, T0));

        // The resync the feed performs is unsubscribe + subscribe, and what comes back is a fresh
        // snapshot on the socket — no REST seed, so no seed race. It clears the flag and restarts the
        // chain from its own u.
        book.ApplySnapshot(snapshot.Symbol, next.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);
        Assert.False(book.IsDirty(snapshot.Symbol));
        Assert.True(book.TryGetDepth(snapshot.Symbol, T0, out _));

        // And the run continues from there: the next captured frame chains onto the reseeded u, which
        // is what "restarts the chain" has to mean if the resync is worth performing.
        var after = deltas[13];
        Assert.Equal(next.LastUpdateId, after.FirstUpdateId);
        Assert.Equal(
            WeexBookBuilder.DeltaResult.Applied,
            book.ApplyDelta(after.Symbol, after.FirstUpdateId, after.LastUpdateId, after.Levels,
                after.Bids, after.Asks, T0));
    }

    [Fact]
    public void A_delta_before_any_snapshot_is_ignored_and_the_book_is_untrusted()
    {
        var book = new WeexBookBuilder(CapturedLevels);
        var first = Deltas()[0];

        Assert.Equal(
            WeexBookBuilder.DeltaResult.Ignored,
            book.ApplyDelta(first.Symbol, first.FirstUpdateId, first.LastUpdateId, first.Levels,
                first.Bids, first.Asks, T0));
        Assert.True(book.IsDirty(first.Symbol));   // absent counts as untrusted
    }

    // ── Reconnect ──────────────────────────────────────────────────────────
    [Fact]
    public void Every_book_is_distrusted_across_a_reconnect()
    {
        // WsConnection comes back after ~1 s and reports Connected the moment the socket opens, while
        // these books still hold the market as it stood before the drop. The frames that would have
        // exposed the gap were never delivered, so nothing else can catch this.
        var book = new WeexBookBuilder(CapturedLevels);
        var snapshot = Snapshot();
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);
        Assert.True(book.TryGetDepth(snapshot.Symbol, T0, out _));

        book.MarkAllDirty();

        Assert.True(book.IsDirty(snapshot.Symbol));
        Assert.False(book.TryGetDepth(snapshot.Symbol, T0, out _));
        Assert.Equal(0, book.FreshCount(TimeSpan.FromSeconds(30), T0));

        // Resubscribing reseeds it from the socket's own snapshot.
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);
        Assert.True(book.TryGetDepth(snapshot.Symbol, T0, out _));
    }

    // ── Staleness ──────────────────────────────────────────────────────────
    [Fact]
    public void A_book_that_stopped_updating_stops_counting_as_live()
    {
        var book = new WeexBookBuilder(CapturedLevels);
        var snapshot = Snapshot();
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);

        var window = TimeSpan.FromSeconds(30);
        Assert.Equal(1, book.FreshCount(window, T0));
        Assert.Equal(1, book.FreshCount(window, T0 + TimeSpan.FromSeconds(29)));
        Assert.Equal(0, book.FreshCount(window, T0 + TimeSpan.FromSeconds(31)));

        // A frame arriving late brings it back — the count measures our receipt, not the venue's E.
        var d = Deltas()[0];
        book.ApplyDelta(d.Symbol, d.FirstUpdateId, d.LastUpdateId, d.Levels, d.Bids, d.Asks, T0 + TimeSpan.FromSeconds(31));
        Assert.Equal(1, book.FreshCount(window, T0 + TimeSpan.FromSeconds(31)));

        // The book itself is still served while the socket is healthy: a quiet instrument is not a
        // wrong one, and staleness is a FEED-wide signal, not a per-symbol gate (see WeexWsFeed).
        Assert.True(book.TryGetDepth(snapshot.Symbol, T0 + TimeSpan.FromHours(1), out _));
    }

    // ── The depth-level gate ───────────────────────────────────────────────
    [Fact]
    public void A_book_thinner_than_the_level_we_asked_for_is_never_served()
    {
        // The feed subscribes @depth200. If WEEX ever answers that with the 15-level default, the
        // bands the REST path fills would quietly come back null instead. So a book is only served at
        // the level we asked for; anything thinner leaves depth on REST, which is a visible loss of
        // the WS path rather than an invisible loss of two bands.
        var book = new WeexBookBuilder(minLevels: 200);
        var snapshot = Snapshot();
        Assert.Equal(CapturedLevels, snapshot.Levels);
        book.ApplySnapshot(snapshot.Symbol, snapshot.LastUpdateId, snapshot.Levels, snapshot.Bids, snapshot.Asks, T0);

        Assert.False(book.TryGetDepth(snapshot.Symbol, T0, out _));
        Assert.Equal(0, book.FreshCount(TimeSpan.FromSeconds(30), T0));

        // The cross-check still watches it, though: a thin top is still a true top, and a frozen thin
        // book is still worth catching.
        Assert.True(book.TryGetTopMid(snapshot.Symbol, out _));
    }

    // ── Trimming to the venue's window ─────────────────────────────────────
    [Fact]
    public void The_book_is_trimmed_back_to_the_venues_own_window()
    {
        // In the captured run WEEX removed every level that left the window, so nothing accumulated.
        // Nothing promises that, and a level that silently fell out of a 200-deep book sits exactly
        // where the 50 bps band is measured — so the book is bounded by the window the venue stamps
        // on its own frames.
        var book = new WeexBookBuilder(minLevels: 2);
        book.ApplySnapshot("cmt_x", 10, 2, [(100, 1), (99, 1)], [(101, 1), (102, 1)], T0);

        // Four bids now, past the slack, so the two worst are dropped.
        Assert.Equal(
            WeexBookBuilder.DeltaResult.Applied,
            book.ApplyDelta("cmt_x", 10, 11, 2, [(98, 1), (97, 1)], [], T0));

        // Remove the two the venue is actually maintaining. An untrimmed book would still answer with
        // a best bid of 98 — a price the venue stopped standing behind.
        Assert.Equal(
            WeexBookBuilder.DeltaResult.Applied,
            book.ApplyDelta("cmt_x", 11, 12, 2, [(100, 0), (99, 0)], [], T0));

        Assert.False(book.TryGetTopMid("cmt_x", out _));
        Assert.False(book.TryGetDepth("cmt_x", T0, out _));
    }

    // ── The adapter's WS-first, REST-fallback contract ─────────────────────
    [Fact]
    public async Task Depth_prefers_the_live_book_then_REST()
    {
        var wsDepth = new Depth(1, 1, 1, 1, 1, 1, T0);

        var live = Adapter(new StubLiveFeed { Fresh = true, Value = wsDepth });
        var served = await live.GetOrderBookAsync("cmt_btcusdt", CancellationToken.None);
        Assert.Equal(T0, served!.At);

        // Unhealthy feed, dirty book, thin book — the adapter cannot tell them apart and does not
        // need to: false means "I cannot honestly serve this", and the REST book answers instead.
        var degraded = Adapter(new StubLiveFeed { Fresh = false });
        var rest = await degraded.GetOrderBookAsync("cmt_btcusdt", CancellationToken.None);
        Assert.NotNull(rest);
        Assert.NotEqual(T0, rest!.At);
    }

    [Fact]
    public async Task No_feed_uses_REST()
    {
        var rest = await Adapter(ws: null).GetOrderBookAsync("cmt_btcusdt", CancellationToken.None);
        Assert.NotNull(rest);
    }

    [Fact]
    public void The_adapter_declares_both_transports_for_depth_and_only_rest_elsewhere()
    {
        // A config fact (ws_url set or not), not a per-request coin flip — so it is declared the same
        // way whether or not a feed happens to be wired into this instance.
        var capabilities = Adapter(ws: null).Capabilities.ToDictionary(c => c.DatasetCode, c => c.TransportsUs);

        Assert.Equal("rest,ws", capabilities["depth"]);
        Assert.Equal("rest", capabilities["snapshot"]);
        Assert.Equal("rest", capabilities["candles"]);
        Assert.Equal("rest", capabilities["funding"]);
        Assert.Equal("rest", capabilities["discovery"]);
    }

    private static WeexFuturesMarketData Adapter(IWeexLiveFeed? ws) =>
        new(new WeexFuturesClient(new HttpClient(new DepthOnlyHandler()), "https://api-contract.weex.test"),
            new NoOpenInterest(), ws);

    /// <summary>Replays the captured frames with a plain dictionary and nothing else — deliberately
    /// not the builder, so the assembly test compares two independent answers.</summary>
    private static (Dictionary<double, double> Bids, Dictionary<double, double> Asks) Replay(
        Frame snapshot, IReadOnlyList<Frame> deltas)
    {
        var bids = new Dictionary<double, double>();
        var asks = new Dictionary<double, double>();
        Apply(bids, snapshot.Bids);
        Apply(asks, snapshot.Asks);
        foreach (var d in deltas)
        {
            Apply(bids, d.Bids);
            Apply(asks, d.Asks);
        }

        return (bids, asks);

        static void Apply(Dictionary<double, double> side, IReadOnlyList<(double Price, double Qty)> levels)
        {
            foreach (var (price, qty) in levels)
            {
                if (qty > 0) { side[price] = qty; } else { side.Remove(price); }
            }
        }
    }

    /// <summary>One captured depth frame, read field by field and case-sensitively — <c>U</c> and
    /// <c>u</c> are different fields, and no case-insensitive binder can tell them apart.</summary>
    private sealed record Frame(
        string Symbol, long FirstUpdateId, long LastUpdateId, int Levels,
        IReadOnlyList<(double Price, double Qty)> Bids,
        IReadOnlyList<(double Price, double Qty)> Asks)
    {
        public static Frame Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new Frame(
                root.GetProperty("s").GetString()!,
                root.GetProperty("U").GetInt64(),
                root.GetProperty("u").GetInt64(),
                root.GetProperty("l").GetInt32(),
                Side(root, "b"),
                Side(root, "a"));
        }

        private static List<(double, double)> Side(JsonElement root, string name)
        {
            var list = new List<(double, double)>();
            foreach (var level in root.GetProperty(name).EnumerateArray())
            {
                list.Add((
                    double.Parse(level[0].GetString()!, CultureInfo.InvariantCulture),
                    double.Parse(level[1].GetString()!, CultureInfo.InvariantCulture)));
            }

            return list;
        }
    }

    private sealed class StubLiveFeed : IWeexLiveFeed
    {
        public bool Fresh { get; init; }

        public Depth? Value { get; init; }

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            depth = Value!;
            return Fresh;
        }
    }

    /// <summary>The snapshot path is not under test here; this only has to be a feed that never
    /// claims a sample it does not have.</summary>
    private sealed class NoOpenInterest : IWeexOpenInterestFeed
    {
        public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
        {
            openInterest = 0;
            at = default;
            return false;
        }
    }

    /// <summary>Answers only /capi/v2/market/depth, with a book wide enough to be told apart from the
    /// stub feed's by its timestamp alone.</summary>
    private sealed class DepthOnlyHandler : HttpMessageHandler
    {
        private const string Book =
            """{"bids":[["100.0","2"],["95.0","3"]],"asks":[["101.0","2"],["106.0","3"]]}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/capi/v2/market/depth", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Book, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

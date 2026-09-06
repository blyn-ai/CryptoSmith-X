using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Binance;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The Binance USDⓈ-M WS book, driven by the frames captured in Fixtures/binance-ws — the same bytes
/// <see cref="BinanceWsProtocolTests"/> pins the protocol with, now run through the code that has to
/// believe them. That file asserts what the venue sends; this one asserts what we do with it.
///
/// Four things are worth failing over, and each has its own test below: the captured run assembles
/// into the book an independent replay produces; the seam between the REST snapshot and the stream is
/// crossed by the first-event rule and would NOT be crossed by the steady-state rule; a missed frame
/// stops the book instead of corrupting it; and a band the snapshot never covered is answered null
/// even when the levels present would let the arithmetic bound it.
/// </summary>
public sealed class BinanceWsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "binance-ws", name);

    private static Snapshot LoadSnapshot() => Snapshot.Parse(File.ReadAllText(FixturePath("depth-snapshot.json")));

    private static List<Frame> LoadDeltas() =>
        [.. File.ReadAllLines(FixturePath("depth-deltas.jsonl")).Where(l => l.Length > 0).Select(Frame.Parse)];

    // ── Assembly across the seam ─────────────────────────────────────────
    [Fact]
    public void The_captured_run_assembles_into_the_book_an_independent_replay_produces()
    {
        // Played in the order the venue actually delivers it: frames first (buffered), then the
        // snapshot that was fetched while they were arriving, then the rest of the stream.
        var books = new BinanceBookBuilder();
        var snapshot = LoadSnapshot();
        var deltas = LoadDeltas();
        var seam = deltas.FindIndex(d => d.LastUpdateId >= snapshot.LastUpdateId);
        Assert.Equal(8, seam);   // eight frames predate the snapshot; the fixture really straddles it

        foreach (var d in deltas.Take(seam))
        {
            Assert.Equal(BinanceBookBuilder.DeltaResult.Buffered, Apply(books, d));
        }

        Assert.True(books.ApplySnapshot("BTCUSDT", snapshot.LastUpdateId, snapshot.Bids, snapshot.Asks, T0));

        foreach (var d in deltas.Skip(seam))
        {
            var result = Apply(books, d);
            Assert.True(
                result is BinanceBookBuilder.DeltaResult.Applied or BinanceBookBuilder.DeltaResult.Ignored,
                $"frame u={d.LastUpdateId} gave {result}");
        }

        // Independently replayed with a plain dictionary — no builder, no sequencing, no windowing —
        // so agreement is evidence about the builder rather than about itself.
        var (expectedBids, expectedAsks) = Replay(snapshot, deltas);
        Assert.True(books.TryGetTopMid("BTCUSDT", out var mid));
        Assert.Equal((expectedBids.Keys.Max() + expectedAsks.Keys.Min()) / 2, mid, 6);
        Assert.Equal(79878.35, mid, 4);
    }

    [Fact]
    public void The_buffer_is_what_makes_the_seam_crossable_at_all()
    {
        // Without buffering, the frames that arrived while the REST snapshot was in flight are gone,
        // and the first frame to show up afterwards starts AFTER the snapshot's lastUpdateId — a
        // real gap, correctly detected, forever. Seeding first and then feeding only the later
        // frames reproduces exactly that, which is what makes "buffer, then fetch" the venue's own
        // documented order and not an optimisation.
        var books = new BinanceBookBuilder();
        var snapshot = LoadSnapshot();
        var deltas = LoadDeltas();
        var seam = deltas.FindIndex(d => d.LastUpdateId >= snapshot.LastUpdateId);

        books.ApplySnapshot("BTCUSDT", snapshot.LastUpdateId, snapshot.Bids, snapshot.Asks, T0);

        // Skip the seam frame — i.e. pretend it was dropped instead of buffered.
        Assert.Equal(BinanceBookBuilder.DeltaResult.Gap, Apply(books, deltas[seam + 1]));
        Assert.True(books.NeedsSeed("BTCUSDT"));
    }

    [Fact]
    public void The_seam_frame_is_accepted_by_the_first_event_rule_that_the_steady_state_rule_would_reject()
    {
        // The discriminating test. This one frame is the entire reason BinanceBookBuilder carries two
        // rules: its pu points at the frame before it, which has nothing to do with the snapshot, so
        // a builder holding only the pu rule would gap here on every single reseed and never
        // assemble a book.
        var snapshot = LoadSnapshot();
        var deltas = LoadDeltas();
        var seamFrame = deltas.First(d => d.LastUpdateId >= snapshot.LastUpdateId);

        Assert.NotEqual(snapshot.LastUpdateId, seamFrame.PreviousUpdateId);   // the pu rule would say Gap
        Assert.True(seamFrame.FirstUpdateId <= snapshot.LastUpdateId);        // the first-event rule says apply
        Assert.True(seamFrame.LastUpdateId >= snapshot.LastUpdateId);

        var books = new BinanceBookBuilder();
        books.ApplySnapshot("BTCUSDT", snapshot.LastUpdateId, snapshot.Bids, snapshot.Asks, T0);
        Assert.Equal(BinanceBookBuilder.DeltaResult.Applied, Apply(books, seamFrame));

        // And the rule switches off after exactly one frame: the next one is judged by pu.
        var next = deltas[deltas.IndexOf(seamFrame) + 1];
        Assert.Equal(seamFrame.LastUpdateId, next.PreviousUpdateId);
        Assert.Equal(BinanceBookBuilder.DeltaResult.Applied, Apply(books, next));
    }

    [Fact]
    public void Frames_already_contained_in_the_snapshot_are_ignored_and_are_not_gaps()
    {
        // The 8 pre-snapshot frames replayed after seeding are work already done, not evidence of a
        // problem — treating them as gaps would make every seed immediately dirty its own book.
        var books = new BinanceBookBuilder();
        var snapshot = LoadSnapshot();
        var deltas = LoadDeltas();

        books.ApplySnapshot("BTCUSDT", snapshot.LastUpdateId, snapshot.Bids, snapshot.Asks, T0);

        foreach (var d in deltas.Where(d => d.LastUpdateId < snapshot.LastUpdateId))
        {
            Assert.Equal(BinanceBookBuilder.DeltaResult.Ignored, Apply(books, d));
        }

        Assert.False(books.NeedsSeed("BTCUSDT"));
    }

    // ── A break stops the book ───────────────────────────────────────────
    [Fact]
    public void A_missed_frame_stops_the_book_rather_than_corrupting_it()
    {
        var books = new BinanceBookBuilder();
        var snapshot = LoadSnapshot();
        var deltas = LoadDeltas();
        var seam = deltas.FindIndex(d => d.LastUpdateId >= snapshot.LastUpdateId);

        books.ApplySnapshot("BTCUSDT", snapshot.LastUpdateId, snapshot.Bids, snapshot.Asks, T0);
        Apply(books, deltas[seam]);
        Apply(books, deltas[seam + 1]);

        // Drop one frame from the middle of the run: the next one's pu no longer matches.
        Assert.Equal(BinanceBookBuilder.DeltaResult.Gap, Apply(books, deltas[seam + 3]));

        // From here nothing is applied and nothing is served until a fresh seed lands — a dirty book
        // yields no depth rather than a wrong one.
        Assert.Equal(BinanceBookBuilder.DeltaResult.Ignored, Apply(books, deltas[seam + 4]));
        Assert.True(books.NeedsSeed("BTCUSDT"));
        Assert.False(books.TryGetDepth("BTCUSDT", T0, out _));
        Assert.False(books.TryGetTopMid("BTCUSDT", out _));
    }

    [Fact]
    public void Every_book_is_distrusted_across_a_reconnect()
    {
        // Whatever the socket missed while it was down is by definition a sequence break we can
        // never observe, because the frame that would have proved it never arrived. The feed calls
        // this before resubscribing, which is also what puts every symbol back in front of the seed
        // loop.
        var books = new BinanceBookBuilder();
        var snapshot = LoadSnapshot();
        books.ApplySnapshot("BTCUSDT", snapshot.LastUpdateId, snapshot.Bids, snapshot.Asks, T0);
        Assert.False(books.NeedsSeed("BTCUSDT"));

        books.MarkAllDirty();

        Assert.True(books.NeedsSeed("BTCUSDT"));
        Assert.False(books.TryGetDepth("BTCUSDT", T0, out _));
    }

    // ── The seeded window ────────────────────────────────────────────────
    [Fact]
    public void Bands_inside_the_seeded_window_are_real_and_bands_beyond_it_are_null()
    {
        // The captured BTCUSDT snapshot is limit=1000 and reaches about 17 bps from mid — the venue's
        // deepest available window on its most liquid symbol. So 10 bps is answerable and 25 and 50
        // are not, and that is not a bug in this feed: it is the true state of our knowledge, and the
        // same answer the REST path gives, only reached honestly instead of by accident.
        var books = new BinanceBookBuilder();
        var snapshot = LoadSnapshot();
        books.ApplySnapshot("BTCUSDT", snapshot.LastUpdateId, snapshot.Bids, snapshot.Asks, T0);

        Assert.True(books.TryGetDepth("BTCUSDT", T0, out var depth));
        Assert.NotNull(depth.Bid10Bps);
        Assert.NotNull(depth.Ask10Bps);
        Assert.True(depth.Bid10Bps > 0);
        Assert.Null(depth.Bid25Bps);
        Assert.Null(depth.Ask25Bps);
        Assert.Null(depth.Bid50Bps);
        Assert.Null(depth.Ask50Bps);
        Assert.Equal(T0, depth.At);
    }

    [Fact]
    public void A_far_level_delivered_by_a_delta_does_not_make_an_uncovered_band_answerable()
    {
        // The defect this guard exists to prevent, in miniature.
        //
        // The diff stream reports levels that CHANGED. Seed a book whose levels span less than 10
        // bps, then let one delta deliver a single level far below: DepthMath now sees a level
        // outside the 10 bps band and calls the band "bounded", and would return the sum of the two
        // levels it happens to hold — a real-looking number covering a price region we have never
        // been told about. The seeded window is what refuses it.
        var books = new BinanceBookBuilder();
        books.ApplySnapshot(
            "TESTUSDT", 100,
            bids: [(100.00, 5), (99.99, 5)],
            asks: [(100.01, 5), (100.02, 5)],
            T0);

        // Nothing is answerable yet: the seed spans well under 10 bps in either direction.
        Assert.True(books.TryGetDepth("TESTUSDT", T0, out var before));
        Assert.Null(before.Bid10Bps);
        Assert.Null(before.Ask10Bps);

        // One far level arrives — legitimately, as a real order — and now the arithmetic alone would
        // bound the band.
        Assert.Equal(
            BinanceBookBuilder.DeltaResult.Applied,
            books.ApplyDelta("TESTUSDT", 100, 101, 100, [(99.00, 7)], [], T0));

        Assert.True(books.TryGetDepth("TESTUSDT", T0, out var after));
        Assert.Null(after.Bid10Bps);   // still null: the 99.99–99.91 region remains unknown to us
        Assert.Null(after.Ask10Bps);
    }

    [Fact]
    public void A_price_that_walks_out_of_the_seeded_window_asks_for_a_fresh_snapshot()
    {
        // A book can be perfectly sequenced and still have gone dark at depth, because the price
        // moved out from under the snapshot that defined what we know. That is not a reason to stop
        // serving — the narrow bands are usually still covered — but it IS a reason to reseed, and
        // the feed expresses it the same way it expresses every other reason: mark it dirty and let
        // the seed loop find it.
        var books = new BinanceBookBuilder();
        books.ApplySnapshot(
            "TESTUSDT", 100,
            bids: [(100.00, 5), (99.00, 5)],
            asks: [(100.01, 5), (101.00, 5)],
            T0);

        // At the seeded mid of 100.005 the 50 bps edges are 99.505 and 100.505, both inside the
        // window the snapshot delivered.
        Assert.False(books.SeedWindowOutgrown("TESTUSDT"));

        // The market moves up: the old best ask is lifted (quantity 0 removes it) and the top of book
        // reforms sixty cents higher. The new mid is 100.605, whose 50 bps ask edge is 101.108 — past
        // the 101.00 the snapshot reached, so the deep ask band has gone dark even though the
        // sequence is perfect.
        books.ApplyDelta("TESTUSDT", 100, 101, 100, [(100.60, 5)], [(100.01, 0), (100.61, 5)], T0);

        Assert.True(books.SeedWindowOutgrown("TESTUSDT"));
    }

    [Fact]
    public void A_zero_quantity_removes_a_level_rather_than_standing_at_zero_size()
    {
        // Binance pads the quantity to the symbol's step precision, so a removal on BTCUSDT arrives
        // as "0.000" and not "0" — see BinanceWsProtocolTests. Handled numerically, so the padding
        // cannot turn 184 removals in one short run into 184 dead levels sitting exactly where the
        // deep bands are measured.
        var books = new BinanceBookBuilder();
        books.ApplySnapshot("TESTUSDT", 100, bids: [(100.00, 5), (99.00, 5)], asks: [(101.00, 5)], T0);
        Assert.True(books.TryGetTopMid("TESTUSDT", out var before));
        Assert.Equal(100.5, before, 6);

        books.ApplyDelta("TESTUSDT", 100, 101, 100, [(100.00, 0)], [], T0);

        Assert.True(books.TryGetTopMid("TESTUSDT", out var after));
        Assert.Equal(100.0, after, 6);   // the best bid is gone, not sitting at size zero
    }

    private static BinanceBookBuilder.DeltaResult Apply(BinanceBookBuilder books, Frame f) =>
        books.ApplyDelta("BTCUSDT", f.FirstUpdateId, f.LastUpdateId, f.PreviousUpdateId, f.Bids, f.Asks, T0);

    /// <summary>A plain-dictionary replay: no builder, no sequencing, no windowing. Agreement with
    /// the builder is then evidence about the builder.</summary>
    private static (Dictionary<double, double> Bids, Dictionary<double, double> Asks) Replay(
        Snapshot snapshot, List<Frame> deltas)
    {
        var bids = snapshot.Bids.Where(l => l.Qty > 0).ToDictionary(l => l.Price, l => l.Qty);
        var asks = snapshot.Asks.Where(l => l.Qty > 0).ToDictionary(l => l.Price, l => l.Qty);

        foreach (var d in deltas.Where(d => d.LastUpdateId >= snapshot.LastUpdateId))
        {
            foreach (var (price, qty) in d.Bids)
            {
                if (qty > 0) { bids[price] = qty; } else { bids.Remove(price); }
            }

            foreach (var (price, qty) in d.Asks)
            {
                if (qty > 0) { asks[price] = qty; } else { asks.Remove(price); }
            }
        }

        return (bids, asks);
    }

    private sealed record Snapshot(
        long LastUpdateId,
        List<(double Price, double Qty)> Bids,
        List<(double Price, double Qty)> Asks)
    {
        public static Snapshot Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new Snapshot(
                root.GetProperty("lastUpdateId").GetInt64(),
                Side(root, "bids"),
                Side(root, "asks"));
        }

        private static List<(double, double)> Side(JsonElement root, string name)
        {
            var list = new List<(double, double)>();
            foreach (var level in root.GetProperty(name).EnumerateArray())
            {
                list.Add((Num(level[0]), Num(level[1])));
            }

            return list;
        }

        private static double Num(JsonElement e) =>
            double.Parse(e.GetString()!, CultureInfo.InvariantCulture);
    }

    private sealed record Frame(
        long FirstUpdateId,
        long LastUpdateId,
        long PreviousUpdateId,
        List<(double Price, double Qty)> Bids,
        List<(double Price, double Qty)> Asks)
    {
        public static Frame Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new Frame(
                root.GetProperty("U").GetInt64(),
                root.GetProperty("u").GetInt64(),
                root.GetProperty("pu").GetInt64(),
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
}

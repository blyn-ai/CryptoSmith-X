using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The WS building blocks: the freshness cache, the shared depth math, and — most of all — the book
/// builder's snapshot/delta/seq handling, which is where a streaming feed goes wrong. Real captured
/// Kraken frames drive the parse-and-apply path; crafted books pin the exact numbers and the edges.
/// </summary>
public sealed class KrakenWsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kraken-ws", name));

    // ── MarketCache ────────────────────────────────────────────────────────
    [Fact]
    public void Cache_serves_fresh_and_hides_stale()
    {
        var clock = new FakeTimeProvider(T0);
        var cache = new MarketCache<int>(clock);
        cache.Set("A", 1);

        Assert.True(cache.TryGet("A", TimeSpan.FromSeconds(30), out var v) && v == 1);
        Assert.Equal(1, cache.FreshCount(TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(cache.TryGet("A", TimeSpan.FromSeconds(30), out _));
        Assert.Equal(0, cache.FreshCount(TimeSpan.FromSeconds(30)));
        Assert.Empty(cache.FresherThan(TimeSpan.FromSeconds(30)));
    }

    // ── DepthMath ──────────────────────────────────────────────────────────
    [Fact]
    public void Depth_math_sums_bands_and_nulls_the_unbounded_one()
    {
        // mid = 100. Same thin book as the REST test, so the two paths provably agree.
        var bids = new[] { (99.99, 2.0), (99.85, 3.0), (99.60, 4.0) };
        var asks = new[] { (100.01, 2.0), (100.15, 3.0), (100.40, 4.0) };

        var depth = DepthMath.Compute(bids, asks, T0);

        Assert.NotNull(depth);
        Assert.Equal(199.98, depth!.Bid10Bps!.Value, 6);
        Assert.Equal(499.53, depth.Bid25Bps!.Value, 6);
        Assert.Null(depth.Bid50Bps);            // nothing beyond 50 bps → undercount → null
        Assert.Equal(200.02, depth.Ask10Bps!.Value, 6);
        Assert.Equal(500.47, depth.Ask25Bps!.Value, 6);
        Assert.Null(depth.Ask50Bps);
    }

    // ── BookBuilder: crafted, exact ────────────────────────────────────────
    [Fact]
    public void Book_snapshot_then_deltas_produce_the_expected_depth()
    {
        var book = new KrakenBookBuilder();
        book.ApplySnapshot("PF_X", 100,
            [(99.99, 2), (99.85, 3), (99.60, 4)],
            [(100.01, 2), (100.15, 3), (100.40, 4)], T0);

        // A contiguous delta: grow the top bid; qty 0 removes a far ask (does not affect bands).
        Assert.Equal(KrakenBookBuilder.DeltaResult.Applied, book.ApplyDelta("PF_X", isBid: true, 101, 99.99, 5, T0));
        Assert.Equal(KrakenBookBuilder.DeltaResult.Applied, book.ApplyDelta("PF_X", isBid: false, 102, 100.40, 0, T0));

        Assert.True(book.TryGetDepth("PF_X", T0, out var depth));
        Assert.Equal(99.99 * 5, depth.Bid10Bps!.Value, 6);   // top bid updated to qty 5
        Assert.Null(depth.Ask50Bps);                          // 100.40 removed; nothing beyond 50 bps
    }

    [Fact]
    public void Book_qty_zero_removes_a_level()
    {
        var book = new KrakenBookBuilder();
        book.ApplySnapshot("PF_X", 1, [(100.0, 1)], [(101.0, 1)], T0);
        book.ApplyDelta("PF_X", isBid: true, 2, 100.0, 0, T0);   // remove the only bid

        // No bids left → no measurable book → no depth.
        Assert.False(book.TryGetDepth("PF_X", T0, out _));
    }

    // ── BookBuilder: seq gap → dirty → resync ──────────────────────────────
    [Fact]
    public void A_sequence_gap_marks_the_book_dirty_until_a_resync_snapshot()
    {
        var book = new KrakenBookBuilder();
        book.ApplySnapshot("PF_X", 10, [(99.0, 1)], [(101.0, 1)], T0);

        // seq jumps 10 → 12: a message was missed.
        Assert.Equal(KrakenBookBuilder.DeltaResult.Gap, book.ApplyDelta("PF_X", isBid: true, 12, 99.0, 2, T0));
        Assert.True(book.IsDirty("PF_X"));
        Assert.False(book.TryGetDepth("PF_X", T0, out _));
        // Further deltas are ignored while dirty.
        Assert.Equal(KrakenBookBuilder.DeltaResult.Ignored, book.ApplyDelta("PF_X", isBid: true, 13, 99.0, 3, T0));

        // A resync snapshot clears it.
        book.ApplySnapshot("PF_X", 50, [(99.0, 1)], [(101.0, 1)], T0);
        Assert.False(book.IsDirty("PF_X"));
        Assert.True(book.TryGetDepth("PF_X", T0, out _));
    }

    [Fact]
    public void A_delta_before_any_snapshot_is_ignored_and_the_book_is_untrusted()
    {
        var book = new KrakenBookBuilder();
        Assert.Equal(KrakenBookBuilder.DeltaResult.Ignored, book.ApplyDelta("PF_X", isBid: true, 1, 100.0, 1, T0));
        Assert.True(book.IsDirty("PF_X"));   // absent counts as untrusted
    }

    [Fact]
    public void A_quiet_clean_book_still_serves_depth()
    {
        // Age is not a gate on the book: an illiquid instrument with no recent delta is still correct.
        // Socket liveness is the feed's concern (it gates on health), not the book's.
        var book = new KrakenBookBuilder();
        book.ApplySnapshot("PF_X", 1, [(99.99, 2), (99.0, 1)], [(100.01, 2), (101.0, 1)], T0);
        Assert.True(book.TryGetDepth("PF_X", T0, out _));
    }

    // ── BookBuilder: real captured Kraken frames ───────────────────────────
    [Fact]
    public void Real_snapshot_and_contiguous_deltas_apply_cleanly()
    {
        var book = new KrakenBookBuilder();

        using var snap = JsonDocument.Parse(Fixture("book_snapshot.json"));
        var root = snap.RootElement;
        var symbol = root.GetProperty("product_id").GetString()!;
        var seq = root.GetProperty("seq").GetInt64();
        var bids = Levels(root.GetProperty("bids"));
        var asks = Levels(root.GetProperty("asks"));
        book.ApplySnapshot(symbol, seq, bids, asks, T0);

        using var deltas = JsonDocument.Parse(Fixture("book_deltas.json"));
        foreach (var d in deltas.RootElement.EnumerateArray())
        {
            var result = book.ApplyDelta(
                d.GetProperty("product_id").GetString()!,
                d.GetProperty("side").GetString() == "buy",
                d.GetProperty("seq").GetInt64(),
                d.GetProperty("price").GetDouble(),
                d.GetProperty("qty").GetDouble(),
                T0);
            Assert.Equal(KrakenBookBuilder.DeltaResult.Applied, result);   // real deltas are contiguous
        }

        Assert.False(book.IsDirty(symbol));
        Assert.True(book.TryGetDepth(symbol, T0, out var depth));
        Assert.NotNull(depth);
    }

    private static List<(double, double)> Levels(JsonElement array)
    {
        var list = new List<(double, double)>();
        foreach (var l in array.EnumerateArray())
        {
            list.Add((l.GetProperty("price").GetDouble(), l.GetProperty("qty").GetDouble()));
        }

        return list;
    }
}

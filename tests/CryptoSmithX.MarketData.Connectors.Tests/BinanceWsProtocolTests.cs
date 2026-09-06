using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoSmithX.MarketData.Connectors.Binance;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// What the VENUE sends, asserted against the bytes in Fixtures/binance-ws.
/// <see cref="BinanceWsTests"/> asserts what we do with them; this file asserts that the protocol is
/// still the one our code believes in.
///
/// It exists because a protocol conclusion that lives only in prose goes stale in silence — the
/// standing example in this repository is commit 100f605, which recorded a correct finding about
/// WEEX's V2 delta format in a commit message and never noticed when the venue replaced it. So the
/// facts our order book depends on are pinned here to real frames, and the next generation of the
/// protocol fails these tests instead of quietly corrupting books.
/// </summary>
public sealed class BinanceWsProtocolTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "binance-ws", name);

    private static JsonDocument Snapshot() => JsonDocument.Parse(File.ReadAllText(FixturePath("depth-snapshot.json")));

    private static List<JsonDocument> Deltas() =>
        [.. File.ReadAllLines(FixturePath("depth-deltas.jsonl")).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l))];

    // ── The two sequencing rules ─────────────────────────────────────────
    [Fact]
    public void The_seam_frame_satisfies_the_first_event_rule_and_no_other_venues_rule()
    {
        // The single most important frame in this directory. It is the one that straddles the REST
        // snapshot, and it is where three plausible implementations diverge:
        //
        //   WEEX V3:       U == previous u                 → rejects it
        //   Binance SPOT:  U <= lastUpdateId + 1 <= u      → rejects it
        //   Binance USDⓈ-M: U <= lastUpdateId <= u          → accepts it
        //
        // A builder holding either of the first two rules would reject the seam, reseed, reject the
        // next seam, and never assemble a book at all — a failure that looks like "the venue is
        // unreliable" rather than "we are reading it wrong".
        using var snapshot = Snapshot();
        var lastUpdateId = snapshot.RootElement.GetProperty("lastUpdateId").GetInt64();

        var deltas = Deltas();
        try
        {
            // Frames older than the snapshot exist and must be droppable by the u < lastUpdateId
            // test alone — if there were none, this fixture would not be exercising the seam at all.
            var stale = deltas.Count(d => d.RootElement.GetProperty("u").GetInt64() < lastUpdateId);
            Assert.Equal(8, stale);

            var seam = deltas.First(d => d.RootElement.GetProperty("u").GetInt64() >= lastUpdateId).RootElement;
            var u = seam.GetProperty("u").GetInt64();
            var first = seam.GetProperty("U").GetInt64();
            var previous = seam.GetProperty("pu").GetInt64();

            Assert.True(first <= lastUpdateId && lastUpdateId <= u, "the USDⓈ-M first-event rule holds");

            // And the discriminating half: the rules we did NOT implement would have rejected it.
            Assert.NotEqual(lastUpdateId, previous);        // the steady-state rule does not apply here
            Assert.NotEqual(lastUpdateId + 1, first);       // nor does Binance spot's
        }
        finally
        {
            foreach (var d in deltas) { d.Dispose(); }
        }
    }

    [Fact]
    public void Every_frame_after_the_first_chains_by_pu_equals_the_previous_u()
    {
        // The steady-state rule, on the real run: 60 frames, 59 adjacent pairs, no exceptions. This
        // is also the fact that makes `pu` load-bearing rather than decorative — USDⓈ-M carries it
        // and spot does not, which is the whole reason the two markets need different code.
        var deltas = Deltas();
        try
        {
            Assert.Equal(60, deltas.Count);
            for (var i = 1; i < deltas.Count; i++)
            {
                Assert.Equal(
                    deltas[i - 1].RootElement.GetProperty("u").GetInt64(),
                    deltas[i].RootElement.GetProperty("pu").GetInt64());
            }
        }
        finally
        {
            foreach (var d in deltas) { d.Dispose(); }
        }
    }

    [Fact]
    public void Levels_are_string_pairs_and_a_zero_quantity_appears_as_a_removal()
    {
        // Both members of a level are quoted strings — the adapter's parser depends on it — and a
        // quantity of zero is how a price level is taken off the book: 184 of the 2131 levels in
        // this run are removals. Note the SPELLING, which is why this is compared numerically and
        // not against the literal "0": Binance pads the quantity to the symbol's step precision, so
        // a removal on BTCUSDT arrives as "0.000". A string comparison would have read every one of
        // those 184 removals as a level standing at zero size, leaving dead prices in the book
        // exactly where the deep bands are measured.
        var deltas = Deltas();
        try
        {
            var zeroes = 0;
            foreach (var d in deltas)
            {
                foreach (var side in new[] { "b", "a" })
                {
                    foreach (var level in d.RootElement.GetProperty(side).EnumerateArray())
                    {
                        Assert.Equal(JsonValueKind.Array, level.ValueKind);
                        Assert.Equal(2, level.GetArrayLength());
                        Assert.Equal(JsonValueKind.String, level[0].ValueKind);
                        Assert.Equal(JsonValueKind.String, level[1].ValueKind);
                        if (double.Parse(level[1].GetString()!) == 0) { zeroes++; }
                    }
                }
            }

            Assert.True(zeroes > 0, "the captured run contains explicit level removals");
        }
        finally
        {
            foreach (var d in deltas) { d.Dispose(); }
        }
    }

    [Fact]
    public void The_snapshot_carries_a_last_update_id_and_a_thousand_levels_a_side()
    {
        // The seed's own extent is what BinanceBookBuilder records as the range the book is COMPLETE
        // within, so "how deep did limit=1000 actually reach" is a protocol fact worth pinning: on
        // BTCUSDT it is about 17 bps, which is why the 25 and 50 bps bands are honestly null there.
        using var snapshot = Snapshot();
        var root = snapshot.RootElement;

        Assert.True(root.GetProperty("lastUpdateId").GetInt64() > 0);
        Assert.Equal(1000, root.GetProperty("bids").GetArrayLength());
        Assert.Equal(1000, root.GetProperty("asks").GetArrayLength());

        var bids = root.GetProperty("bids").EnumerateArray().Select(l => double.Parse(l[0].GetString()!)).ToList();
        var asks = root.GetProperty("asks").EnumerateArray().Select(l => double.Parse(l[0].GetString()!)).ToList();
        var mid = (bids.Max() + asks.Min()) / 2;

        var bidReachBps = (mid - bids.Min()) / mid * 10_000;
        var askReachBps = (asks.Max() - mid) / mid * 10_000;
        Assert.InRange(bidReachBps, 15, 20);
        Assert.InRange(askReachBps, 15, 20);
    }

    // ── The case-sensitivity contract ────────────────────────────────────
    [Fact]
    public void A_depth_frame_binds_under_this_connectors_options()
    {
        // The frame carries two pairs of fields differing only in case — e/E and u/U — and this is
        // the test that says our options object handles them.
        var deltas = Deltas();
        try
        {
            var frame = deltas[0].RootElement.Deserialize<BinanceWsDepth>(BinanceJson.Options);

            Assert.NotNull(frame);
            Assert.Equal("depthUpdate", frame!.EventType);          // "e"
            Assert.True(frame.EventTime > 1_700_000_000_000);       // "E" — a millisecond clock, not the type
            Assert.Equal("BTCUSDT", frame.Symbol);
            Assert.True(frame.FirstUpdateId > 0);                   // "U"
            Assert.True(frame.LastUpdateId >= frame.FirstUpdateId); // "u"
            Assert.True(frame.PreviousUpdateId > 0);                // "pu"
            Assert.NotEqual(frame.FirstUpdateId, frame.LastUpdateId);
        }
        finally
        {
            foreach (var d in deltas) { d.Dispose(); }
        }
    }

    [Fact]
    public void The_same_frame_cannot_be_bound_with_the_web_defaults_every_other_connector_uses()
    {
        // The regression test for the reason BinanceJson exists at all. JsonSerializerDefaults.Web
        // turns on PropertyNameCaseInsensitive, under which "e" and "E" — and "u" and "U" — resolve
        // to the same name, and System.Text.Json refuses to build the converter rather than picking
        // one. It throws when the TYPE is first used, not when a surprising value arrives, so this
        // would have been a hard failure on the first frame in production.
        //
        // A private copy of the record is used rather than BinanceWsDepth itself: the serializer
        // caches type metadata per options instance, and the point here is the shape of the type,
        // not which type happens to carry it.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string frame = """{"e":"depthUpdate","E":1788696020835,"U":1,"u":2}""";

        var error = Assert.Throws<InvalidOperationException>(
            () => JsonSerializer.Deserialize<CaseCollidingFrame>(frame, web));

        Assert.Contains("JSON property name", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The shape of a Binance frame, reduced to the collision: two properties whose wire
    /// names differ only in case.</summary>
    private sealed record CaseCollidingFrame
    {
        [JsonPropertyName("e")] public string EventType { get; init; } = "";

        [JsonPropertyName("E")] public long EventTime { get; init; }
    }
}

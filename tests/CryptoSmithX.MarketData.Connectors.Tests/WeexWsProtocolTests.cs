using System.Text.Json;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Pins the WEEX contract V3 public WS protocol against frames captured from the live socket
/// (Fixtures/weex-ws — see the README there for the capture metadata).
///
/// This file exists because the repository has already been burned once by an unrecorded protocol
/// fact: commit 100f605 deferred a WEEX WS feed on the strength of V2's chained
/// startVersion/endVersion deltas, that conclusion lived only in a commit message, and it went
/// stale silently when WEEX shipped V3 and retired V2. So the assertions below are deliberately
/// generation-discriminating rather than merely permissive: swap the fixtures for frames from V2,
/// or from Binance's identically-shaped but differently-chained protocol, and these tests fail.
///
/// No feed code is asserted here. There is none yet, by design — the protocol becomes evidence in
/// the tree before a line of C# depends on it.
/// </summary>
public sealed class WeexWsProtocolTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "weex-ws", name);

    private static JsonDocument Frame(string name) =>
        JsonDocument.Parse(File.ReadAllText(FixturePath(name)));

    /// <summary>The captured delta run, in arrival order, one frame per line.</summary>
    private static List<JsonDocument> Deltas() =>
        [.. File.ReadAllLines(FixturePath("depth-deltas.jsonl"))
              .Where(l => l.Length > 0)
              .Select(l => JsonDocument.Parse(l))];

    // ── The chaining rule ──────────────────────────────────────────────────
    // Each depth frame's U equals the PREVIOUS frame's u — not u + 1 — seeded by the u of the
    // depthSnapshot that the socket itself delivers. A mismatch means frames were missed and the
    // book must resynchronise from a fresh snapshot.
    [Fact]
    public void Depth_deltas_chain_U_to_the_previous_frames_u()
    {
        using var snapshot = Frame("depth-snapshot.json");
        var deltas = Deltas();
        try
        {
            // A run this long is what makes the rule a rule rather than a coincidence: a single
            // pair could chain under several different conventions at once.
            Assert.True(deltas.Count >= 40, $"captured only {deltas.Count} deltas; the rule needs a run");

            var previousU = snapshot.RootElement.GetProperty("u").GetInt64();
            Assert.True(snapshot.RootElement.GetProperty("U").GetInt64() < previousU,
                "within a frame U must be strictly below u");

            for (var i = 0; i < deltas.Count; i++)
            {
                var frame = deltas[i].RootElement;
                var u = frame.GetProperty("U").GetInt64();
                var uu = frame.GetProperty("u").GetInt64();

                Assert.True(u == previousU,
                    $"delta {i} broke the chain: U={u}, previous frame's u={previousU}");
                Assert.True(u < uu, $"delta {i}: U={u} must be strictly below u={uu}");

                previousU = uu;
            }
        }
        finally
        {
            foreach (var d in deltas) d.Dispose();
        }
    }

    /// <summary>
    /// The rule is equality against the previous u, NOT Binance's U &lt;= lastUpdateId + 1 &lt;= u.
    /// If the fixtures were ever replaced by Binance USDⓈ-M frames — which carry the same letters
    /// in the same envelope and would sail through a loose "ids advance" check — this fails.
    /// </summary>
    [Fact]
    public void Chaining_is_equality_not_binances_off_by_one()
    {
        using var snapshot = Frame("depth-snapshot.json");
        var deltas = Deltas();
        try
        {
            var previousU = snapshot.RootElement.GetProperty("u").GetInt64();
            var gapOfOne = 0;
            foreach (var d in deltas)
            {
                if (d.RootElement.GetProperty("U").GetInt64() == previousU + 1) gapOfOne++;
                previousU = d.RootElement.GetProperty("u").GetInt64();
            }

            Assert.Equal(0, gapOfOne);
        }
        finally
        {
            foreach (var d in deltas) d.Dispose();
        }
    }

    /// <summary>
    /// V2's chained startVersion/endVersion deltas were the stated reason WS was deferred. V3 does
    /// not carry those names anywhere. This is the assertion that would have caught the staleness.
    /// </summary>
    [Fact]
    public void No_frame_carries_the_retired_V2_version_fields()
    {
        using var snapshot = Frame("depth-snapshot.json");
        var deltas = Deltas();
        try
        {
            foreach (var root in deltas.Select(d => d.RootElement).Prepend(snapshot.RootElement))
            {
                foreach (var name in new[] { "startVersion", "endVersion", "version" })
                    Assert.False(root.TryGetProperty(name, out _), $"V2 field '{name}' is present");

                // V3 names the generation on every frame; V2 had no 'e' discriminator at all.
                Assert.True(root.TryGetProperty("e", out _));
            }
        }
        finally
        {
            foreach (var d in deltas) d.Dispose();
        }
    }

    // ── Frame shapes ───────────────────────────────────────────────────────
    [Fact]
    public void Depth_frames_have_the_captured_V3_shape()
    {
        using var snapshot = Frame("depth-snapshot.json");
        var snap = snapshot.RootElement;
        Assert.Equal("depthSnapshot", snap.GetProperty("e").GetString());
        Assert.Equal("SNAPSHOT", snap.GetProperty("d").GetString());
        Assert.Equal("BTCUSDT", snap.GetProperty("s").GetString());
        // Plain @depth returns level 15, which is how we know 15 is the default.
        Assert.Equal(15, snap.GetProperty("l").GetInt32());
        Assert.Equal(15, snap.GetProperty("b").GetArrayLength());

        var deltas = Deltas();
        try
        {
            foreach (var d in deltas)
            {
                var f = d.RootElement;
                Assert.Equal("depth", f.GetProperty("e").GetString());
                Assert.Equal("CHANGED", f.GetProperty("d").GetString());
                Assert.Equal("BTCUSDT", f.GetProperty("s").GetString());
                Assert.Equal(15, f.GetProperty("l").GetInt32());
                // E is a NUMBER on data frames, while the greeting's 'time' is a string. Binding
                // both to one type would fail on one of them.
                Assert.Equal(JsonValueKind.Number, f.GetProperty("E").ValueKind);
            }
        }
        finally
        {
            foreach (var d in deltas) d.Dispose();
        }
    }

    /// <summary>
    /// Prices and quantities are strings, and a quantity of exactly "0" is a removal of that price
    /// level rather than a level standing at zero size. Both facts are load-bearing for a book
    /// builder, so both are asserted against the capture rather than trusted.
    /// </summary>
    [Fact]
    public void Levels_are_string_pairs_and_qty_zero_appears_as_a_removal()
    {
        var deltas = Deltas();
        try
        {
            var removals = 0;
            foreach (var d in deltas)
            {
                foreach (var side in new[] { "b", "a" })
                {
                    foreach (var level in d.RootElement.GetProperty(side).EnumerateArray())
                    {
                        Assert.Equal(2, level.GetArrayLength());
                        Assert.Equal(JsonValueKind.String, level[0].ValueKind);
                        Assert.Equal(JsonValueKind.String, level[1].ValueKind);
                        if (level[1].GetString() == "0") removals++;
                    }
                }
            }

            // The run has to actually exercise removals, or the fixture cannot demonstrate them.
            Assert.True(removals > 0, "the captured run contains no qty \"0\" removal");
        }
        finally
        {
            foreach (var d in deltas) d.Dispose();
        }
    }

    [Fact]
    public void Greeting_ack_and_reject_have_the_captured_shape()
    {
        using var greeting = Frame("connect-greeting.json");
        Assert.Equal("connected", greeting.RootElement.GetProperty("event").GetString());
        // Epoch millis as a STRING here, unlike E on data frames.
        Assert.Equal(JsonValueKind.String, greeting.RootElement.GetProperty("time").ValueKind);
        Assert.False(string.IsNullOrEmpty(greeting.RootElement.GetProperty("cid").GetString()));

        using var ack = Frame("subscribe-ack.json");
        Assert.True(ack.RootElement.GetProperty("result").GetBoolean());
        Assert.Equal(1, ack.RootElement.GetProperty("id").GetInt32());

        using var unsub = Frame("unsubscribe-ack.json");
        Assert.True(unsub.RootElement.GetProperty("result").GetBoolean());
        Assert.Equal(1002, unsub.RootElement.GetProperty("id").GetInt32());

        // A rejected channel is a result:false with a msg, on the same envelope as the ack — not a
        // transport error and not a distinct frame type. A feed must read msg to know what failed,
        // and must NOT retry: six rejects on one socket close it with 1007
        // (invalid-channel-close.txt), taking every other subscription down with it.
        using var reject = Frame("subscribe-error.json");
        Assert.False(reject.RootElement.GetProperty("result").GetBoolean());
        Assert.Equal(99, reject.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("INVALID_ARGUMENT: invalid event : totally_bogus_channel",
            reject.RootElement.GetProperty("msg").GetString());
    }

    [Fact]
    public void Ticker_and_kline_frames_wrap_their_payload_in_d()
    {
        using var ticker = Frame("ticker.json");
        Assert.Equal("ticker", ticker.RootElement.GetProperty("e").GetString());
        var t = ticker.RootElement.GetProperty("d");
        Assert.Equal(JsonValueKind.Array, t.ValueKind);
        Assert.Equal(1, t.GetArrayLength());
        Assert.Equal("79982.2", t[0].GetProperty("c").GetString());
        // m and i — mark and index price — are WEEX additions the Binance ticker has no room for.
        Assert.Equal(JsonValueKind.String, t[0].GetProperty("m").ValueKind);
        Assert.Equal(JsonValueKind.String, t[0].GetProperty("i").ValueKind);

        using var kline = Frame("kline.json");
        Assert.Equal("kline", kline.RootElement.GetProperty("e").GetString());
        Assert.Equal("LAST_PRICE", kline.RootElement.GetProperty("p").GetString());
        var k = kline.RootElement.GetProperty("d");
        Assert.Equal(1, k.GetArrayLength());
        Assert.Equal("1m", k[0].GetProperty("i").GetString());
        // 60_000 ms apart: t is the open, T the close, both epoch millis as numbers.
        Assert.Equal(60_000, k[0].GetProperty("T").GetInt64() - k[0].GetProperty("t").GetInt64());

        using var history = Frame("kline-snapshot.json");
        Assert.Equal("klineSnapshot", history.RootElement.GetProperty("e").GetString());
        Assert.True(history.RootElement.GetProperty("d").GetArrayLength() > 1);
    }
}

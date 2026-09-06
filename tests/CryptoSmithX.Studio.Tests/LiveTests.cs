using CryptoSmithX.Studio.Live;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoSmithX.Studio.Tests;

/// <summary>
/// The live stream, in the three places where it decides something.
///
/// The stream itself — a socket held open, HTML pushed down it — is not tested here and could not
/// usefully be: it needs a database, a browser and a collector finishing a pass. What is tested is
/// everything the stream would get WRONG silently, because a live page that is wrong looks exactly
/// like a market that is quiet.
/// </summary>
public sealed class LivePayloadTests
{
    [Fact]
    public void A_pass_carries_its_segment_and_its_collector()
    {
        Assert.True(LiveNotifier.TryReadPass(
            """{"segment": "weex-futures", "collector": "depth"}""", out var segment, out var collector));
        Assert.Equal("weex-futures", segment);
        Assert.Equal("depth", collector);
    }

    /// <summary>
    /// 0019 sends a null collector for "the segment row or its whole policy matrix changed". That is
    /// a real event with a real consequence on this page — the freshness windows come out of the
    /// policy — so it must parse, not fail.
    /// </summary>
    [Fact]
    public void A_policy_change_carries_a_segment_and_no_collector()
    {
        Assert.True(LiveNotifier.TryReadPass(
            """{"segment": "kraken-futures", "collector": null}""", out var segment, out var collector));
        Assert.Equal("kraken-futures", segment);
        Assert.Null(collector);
    }

    /// <summary>
    /// Anything unparseable ends as a false. It must never throw: the parse runs on the notifier's
    /// own read loop, and an exception there would take the loop down — which, from the page, is
    /// indistinguishable from a market where nothing is happening.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("\"weex-futures\"")]
    [InlineData("{}")]
    [InlineData("""{"collector": "snapshot"}""")]
    [InlineData("""{"segment": null}""")]
    [InlineData("""{"segment": 7}""")]
    public void A_payload_it_does_not_understand_is_refused_rather_than_thrown(string payload)
    {
        Assert.False(LiveNotifier.TryReadPass(payload, out var segment, out _));
        Assert.Null(segment);
    }

    /// <summary>
    /// 0015 wrote the key as <c>exchange</c> and 0019 renamed it to <c>segment</c>. The old shape is
    /// refused rather than quietly accepted: a database still sending it would be behind the schema
    /// this application refuses to start on, so accepting it here would only hide that.
    /// </summary>
    [Fact]
    public void The_pre_0019_payload_shape_is_not_accepted()
    {
        Assert.False(LiveNotifier.TryReadPass(
            """{"exchange": "weex-futures", "collector": "snapshot"}""", out _, out _));
    }
}

/// <summary>
/// Which passes redraw a pair page. Every one of these is a product decision rather than a
/// performance tweak: a redraw the page did not need still replaces the reader's table, and a redraw
/// for a dataset the page does not show is the page claiming it updated something it did not.
/// </summary>
public sealed class LiveRelevanceTests
{
    [Theory]
    [InlineData("snapshot")]        // twelve of the fourteen cells
    [InlineData("depth")]           // the other two, and the only cells that can hold a dash
    [InlineData("open_interest")]   // disabled today; must still move the page where it is turned on
    [InlineData("discovery")]       // which venues are rows at all, and the halt / post_only tag
    public void A_pass_that_writes_what_the_table_shows_redraws_it(string collector) =>
        Assert.True(LiveRelevance.Redraws(collector));

    /// <summary>
    /// The candle panels are not redrawn by the live path — the chart library owns those nodes and
    /// they are hourly bars — so a candles pass must not push an update that claims otherwise.
    /// Funding is the history table, which 0025 does not grant and this page does not read: the rate
    /// it prints comes from the snapshot row.
    /// </summary>
    [Theory]
    [InlineData("candles")]
    [InlineData("rollup")]
    [InlineData("funding")]
    public void A_pass_this_page_does_not_draw_leaves_it_alone(string collector) =>
        Assert.False(LiveRelevance.Redraws(collector));

    /// <summary>
    /// A policy or segment change redraws. The windows every figure on the page is judged against
    /// come from <c>segment_dataset.interval_s → dataset.default_interval_s</c>, so an operator
    /// moving the depth interval changes what is late here without a single figure moving.
    /// </summary>
    [Fact]
    public void A_policy_change_redraws_because_it_moves_the_windows() =>
        Assert.True(LiveRelevance.Redraws(null));

    /// <summary>
    /// A dataset nobody has thought about yet does not silently start repainting a public page. The
    /// same direction 0025 takes with grants: arriving on this surface is a decision somebody makes
    /// out loud, in this case by adding a line to the switch.
    /// </summary>
    [Fact]
    public void A_dataset_nobody_has_decided_about_does_not_redraw() =>
        Assert.False(LiveRelevance.Redraws("liquidations"));

    [Fact]
    public void A_pass_on_a_venue_this_page_does_not_show_is_ignored()
    {
        var page = new HashSet<string>(["kraken-futures", "weex-futures"], StringComparer.Ordinal);
        Assert.False(LiveRelevance.Matters(new LiveEvent("binance-usdm", "snapshot", true), page));
        Assert.True(LiveRelevance.Matters(new LiveEvent("weex-futures", "snapshot", true), page));
    }

    /// <summary>
    /// Segment codes are compared exactly, as they are everywhere else on this surface: 0024 says
    /// the codes are stored in the spelling the venue uses and the comparison is case-significant.
    /// </summary>
    [Fact]
    public void Segment_codes_are_matched_exactly()
    {
        var page = new HashSet<string>(["weex-futures"], StringComparer.Ordinal);
        Assert.False(LiveRelevance.Matters(new LiveEvent("WEEX-FUTURES", "snapshot", true), page));
    }

    /// <summary>
    /// A state change carries no segment, and "no segment" is not "every segment". The stream reads
    /// those events separately — they tell the reader the signal died — and a wildcard reading here
    /// would redraw the whole page every time the database connection blinked.
    /// </summary>
    [Fact]
    public void A_connection_state_change_is_not_a_pass_on_every_venue()
    {
        var page = new HashSet<string>(["weex-futures"], StringComparer.Ordinal);
        Assert.False(LiveRelevance.Matters(new LiveEvent(null, null, false), page));
    }
}

/// <summary>The ceiling on simultaneous streams. Its whole purpose is to turn "the public page got
/// slow" into "the page said the live feed was full", so the counting has to be exact.</summary>
public sealed class LiveStreamGateTests
{
    [Fact]
    public void It_admits_up_to_the_ceiling_and_then_refuses()
    {
        var gate = new LiveStreamGate(3);

        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.Equal(3, gate.Open);
    }

    /// <summary>A reader who closes the tab gives the slot back. Without this the ceiling is reached
    /// once, by the first hundred visitors of the week, and never released.</summary>
    [Fact]
    public void A_stream_that_ends_gives_its_slot_back()
    {
        var gate = new LiveStreamGate(1);

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        gate.Exit();

        Assert.Equal(0, gate.Open);
        Assert.True(gate.TryEnter());
    }

    /// <summary>
    /// A hundred arrivals at once must not push the count past the ceiling, even briefly. An
    /// increment-then-check would: the count goes over, the extra streams are refused and hand it
    /// back, and in between the gate reported a number it is supposed to make impossible.
    /// </summary>
    [Fact]
    public void A_burst_of_arrivals_cannot_push_it_over_the_ceiling()
    {
        var gate = new LiveStreamGate(10);
        var admitted = 0;
        var peak = 0;

        Parallel.For(0, 200, _ =>
        {
            if (gate.TryEnter())
            {
                Interlocked.Increment(ref admitted);
            }

            var open = gate.Open;
            if (open > Volatile.Read(ref peak))
            {
                Volatile.Write(ref peak, open);
            }
        });

        Assert.Equal(10, admitted);
        Assert.True(peak <= 10, $"the gate reported {peak} open streams against a ceiling of 10");
    }
}

/// <summary>
/// The notifier's shape: no connection until somebody is watching, and a database it cannot reach
/// does not take the request that asked for it down.
/// </summary>
public sealed class LiveNotifierTests
{
    // Port 1 on loopback: nothing listens there, and nothing is expected to. These tests are about
    // what happens when the database is not reachable, which is the case the page has to survive
    // without lying about the market.
    private static LiveNotifier Unreachable() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] =
                    "Host=127.0.0.1;Port=1;Database=marketdata;Username=studio_reader;Password=studio_reader;Timeout=1",
            })
            .Build(),
        NullLogger<LiveNotifier>.Instance);

    [Fact]
    public void Nothing_is_open_until_somebody_watches()
    {
        var notifier = Unreachable();

        Assert.Equal(0, notifier.Subscribers);
        Assert.False(notifier.Listening);
    }

    /// <summary>
    /// Subscribe is called on the request path of an anonymous stream. A database that is down must
    /// come back as "the signal is down" — which the page then says in words — and never as an
    /// exception out of the endpoint.
    /// </summary>
    [Fact]
    public async Task A_database_it_cannot_reach_is_reported_rather_than_thrown()
    {
        var notifier = Unreachable();

        using (notifier.Subscribe(_ => { }))
        {
            Assert.Equal(1, notifier.Subscribers);
            await Task.Delay(150);
            Assert.False(notifier.Listening);
        }

        Assert.Equal(0, notifier.Subscribers);
    }

    /// <summary>The count is what decides whether the connection is held, so leaving twice must not
    /// take it below the number of tabs actually watching.</summary>
    [Fact]
    public void Leaving_twice_does_not_count_twice()
    {
        var notifier = Unreachable();

        var first = notifier.Subscribe(_ => { });
        using var second = notifier.Subscribe(_ => { });

        first.Dispose();
        first.Dispose();

        Assert.Equal(1, notifier.Subscribers);
    }
}

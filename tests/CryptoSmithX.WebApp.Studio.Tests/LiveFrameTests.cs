using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Live;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// What a room decides on every tick, without the timer or the channels: which figures a socket is
/// allowed to replace, what that does to the marks, and what is worth putting on the wire.
///
/// The rules under test are the ones a reader would never see break. An overlay that runs backwards
/// shows a stale price under a fresh age; a rank left uncomputed leaves a MAX chip on a venue that
/// no longer holds the maximum — which is worse than a stale figure, because it is a claim rather
/// than a lag.
/// </summary>
public sealed class LiveFrameTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 30, TimeSpan.Zero);
    private static readonly DateTime Written = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_quote_older_than_the_written_row_does_not_overlay_it()
    {
        // The plan's rule (§6): the later OBSERVATION wins, never the later arrival. A socket that
        // reconnects can hand over a value it cached before the collector's last pass, and letting
        // that through would make the page go backwards under the reader.
        var rows = Rows.Live(Row(1, bid: 100));
        var stale = Quote(1, bid: 999, at: Written.AddSeconds(-10));

        var overlaid = LiveFrames.Overlay(rows, new Dictionary<int, HubQuote> { [1] = stale }, Now);

        Assert.Equal(100, overlaid[0].Row.BidPrice);
    }

    [Fact]
    public void A_quote_newer_than_the_written_row_replaces_it()
    {
        var rows = Rows.Live(Row(1, bid: 100));
        var fresh = Quote(1, bid: 105, at: Written.AddSeconds(10));

        var overlaid = LiveFrames.Overlay(rows, new Dictionary<int, HubQuote> { [1] = fresh }, Now);

        Assert.Equal(105, overlaid[0].Row.BidPrice);

        // And the PRICE age becomes the venue's own observation age — 20 s here, not the 30 s since
        // the record. The other two calls are untouched: no socket wrote them.
        Assert.Equal(20, overlaid[0].Ages.PriceSeconds);
        Assert.Equal(rows[0].Ages.DepthSeconds, overlaid[0].Ages.DepthSeconds);
    }

    [Fact]
    public void A_field_the_venues_socket_does_not_carry_keeps_the_written_figure()
    {
        // WEEX publishes no mark price at all. Blanking that cell in live mode would turn a venue's
        // silence into our own missing measurement, which is exactly the confusion the whole page
        // is built to prevent.
        var rows = Rows.Live(Row(1, bid: 100) with { MarkPrice = 70_000 });
        var partial = Quote(1, bid: 105, at: Written.AddSeconds(10));   // mark is null on the wire

        var overlaid = LiveFrames.Overlay(rows, new Dictionary<int, HubQuote> { [1] = partial }, Now);

        Assert.Equal(105, overlaid[0].Row.BidPrice);
        Assert.Equal(70_000, overlaid[0].Row.MarkPrice);
    }

    [Fact]
    public void A_row_never_observed_at_all_takes_whatever_the_socket_has()
    {
        // A figure beats a dash: there is no written observation to lose to.
        var rows = Rows.Live(Row(1, bid: null) with { ReceivedAt = null });
        var quote = Quote(1, bid: 105, at: Written);

        var overlaid = LiveFrames.Overlay(rows, new Dictionary<int, HubQuote> { [1] = quote }, Now);

        Assert.Equal(105, overlaid[0].Row.BidPrice);
    }

    [Fact]
    public void Marks_move_when_one_venue_moves()
    {
        // The reason Verdicts.Compute runs again inside the tick rather than once at load: a live
        // figure under a mark computed for the previous one is a claim, not a lag.
        var rows = Rows.Live(Row(1, turnover: 100), Row(2, turnover: 200));
        Assert.Equal(Verdict.Best, Verdicts.Compute(rows).Of(2, PairColumn.Turnover24h));

        var overlaid = LiveFrames.Overlay(
            rows,
            new Dictionary<int, HubQuote> { [1] = Quote(1, bid: null, at: Written.AddSeconds(10), turnover: 900) },
            Now);

        Assert.Equal(Verdict.Best, Verdicts.Compute(overlaid).Of(1, PairColumn.Turnover24h));
    }

    [Fact]
    public void A_slot_says_which_source_wrote_it_and_carries_both_ages()
    {
        // Two ages, always: the live one is what the market is doing, the written one is what a
        // backtest will see, and hiding the second would make the page prettier and less true.
        var rows = Rows.Live(Row(1, bid: 100), Row(2, bid: 100));
        var quotes = new Dictionary<int, HubQuote> { [1] = Quote(1, bid: 105, at: Written.AddSeconds(10)) };
        var overlaid = LiveFrames.Overlay(rows, quotes, Now);

        var slots = LiveFrames.Slots(overlaid, Verdicts.Compute(overlaid), quotes, Now);

        var wired = slots.First(s => s.InstrumentId == 1);
        Assert.Equal("ws", wired.Source);
        Assert.Equal(Format.SpacedAge(20), wired.LiveAge);
        Assert.Equal(Format.SpacedAge(30), wired.WrittenAge);

        // The venue with no socket stays on the database in live mode, and says so.
        var unwired = slots.First(s => s.InstrumentId == 2);
        Assert.Equal("latest", unwired.Source);
        Assert.Equal(Format.Dash, unwired.LiveAge);
    }

    [Fact]
    public void A_paired_column_carries_both_halves_as_their_own_slots()
    {
        // Bid and ask share a cell; they were never one measurement, and each keeps its own rank.
        var rows = Rows.Live(Row(1, bid: 100, ask: 101));
        var slots = LiveFrames.Slots(rows, Verdicts.Compute(rows), Empty, Now);

        var bidCell = slots.Where(s => s.Group == "Bid").ToArray();

        Assert.Equal(2, bidCell.Length);
        Assert.Equal([0, 1], bidCell.Select(s => s.Part).ToArray());
    }

    [Fact]
    public void Only_the_slots_that_moved_travel()
    {
        var rows = Rows.Live(Row(1, bid: 100), Row(2, bid: 200));
        var first = LiveFrames.Slots(rows, Verdicts.Compute(rows), Empty, Now);
        var sent = first.ToDictionary(s => s.Key);

        // Nothing changed: at five frames a second, resending the table would rewrite every cell in
        // the reader's browser for the nothing that happened.
        Assert.Empty(LiveFrames.Diff(sent, first));

        var moved = Rows.Live(Row(1, bid: 111), Row(2, bid: 200));
        var second = LiveFrames.Slots(moved, Verdicts.Compute(moved), Empty, Now);
        var changed = LiveFrames.Diff(sent, second);

        Assert.NotEmpty(changed);
        Assert.All(changed, s => Assert.Equal(1, s.InstrumentId));
    }

    [Fact]
    public void A_slot_this_viewer_has_never_been_sent_always_travels() =>
        Assert.NotEmpty(LiveFrames.Diff(
            new Dictionary<(int, string, int), LiveSlot>(),
            LiveFrames.Slots(Rows.Live(Row(1, bid: 100)), VerdictTable.Empty, Empty, Now)));

    private static readonly Dictionary<int, HubQuote> Empty = [];

    private static PairVenueRow Row(int id, double? bid = null, double? ask = null, double? turnover = null) =>
        Rows.Venue(id, bid: bid, ask: ask, turnover: turnover) with { ReceivedAt = Written };

    private static HubQuote Quote(int id, double? bid, DateTime at, double? turnover = null) =>
        new(id, "kraken-futures", new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
            bid, null, null, null, null, null, null, null, null, turnover);
}

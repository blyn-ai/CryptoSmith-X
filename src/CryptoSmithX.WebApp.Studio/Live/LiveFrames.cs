using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Live;

/// <summary>
/// Turning "what the sockets are holding" into "what changed on this page" — the whole of a room's
/// thinking, kept pure so it can be reasoned about and tested without a timer, a channel or a
/// connection.
///
/// Three steps, in this order and for a reason:
///
///   1. OVERLAY. A live quote replaces a written figure only where it is genuinely newer. The rule
///      is the plan's own (§6): the later OBSERVATION wins, not the later arrival. A socket
///      reconnecting can hand over a value it cached before the collector's last pass, and letting
///      that overwrite the fresher record would make the page go backwards.
///   2. RANK. <see cref="Verdicts.Compute"/> runs again on the overlaid rows. Skipping it would
///      leave live figures under marks computed for the previous ones — a MAX chip sitting on a
///      venue that no longer holds the maximum, which is worse than an old figure because it is a
///      claim rather than a lag.
///   3. FORMAT AND DIFF. Every cell is written by the same code the first paint uses, then compared
///      with what this viewer was last sent. Only the difference travels.
/// </summary>
public static class LiveFrames
{
    /// <summary>The rows as the page should now read them: written figures where the record is
    /// still the freshest thing we have, socket figures where it is not.</summary>
    public static IReadOnlyList<VenueRowModel> Overlay(
        IReadOnlyList<VenueRowModel> rows,
        IReadOnlyDictionary<int, HubQuote> quotes,
        DateTimeOffset now)
    {
        var overlaid = new List<VenueRowModel>(rows.Count);
        foreach (var r in rows)
        {
            overlaid.Add(quotes.TryGetValue(r.Row.InstrumentId, out var q) && Wins(q, r.Row.ReceivedAt)
                ? r with
                {
                    Row = Apply(r.Row, q),

                    // The PRICE call's age becomes the venue's own observation age; the other two
                    // calls are untouched, because no socket wrote them. Three clocks on a row was
                    // the whole point of CallAges and the live path does not get to collapse them.
                    Ages = r.Ages with { PriceSeconds = (now - q.At).TotalSeconds },
                }
                : r);
        }

        return overlaid;
    }

    /// <summary>Whether a live quote is newer than what was written. A row never observed at all
    /// loses to anything: a figure is better than a dash.</summary>
    private static bool Wins(HubQuote quote, DateTime? receivedAt) =>
        receivedAt is not { } written || quote.At >= new DateTimeOffset(DateTime.SpecifyKind(written, DateTimeKind.Utc));

    /// <summary>
    /// The socket's figures over the record's, FIELD BY FIELD and only where the socket has one.
    ///
    /// Null on a quote means "this venue's socket does not carry this" (see <see cref="HubQuote"/>),
    /// so the written value stays — WEEX publishes no mark price at all, and blanking that cell in
    /// live mode would turn a venue's silence into our own missing measurement.
    /// </summary>
    private static PairVenueRow Apply(PairVenueRow row, HubQuote q) => row with
    {
        BidPrice = q.BidPrice ?? row.BidPrice,
        BidSize = q.BidSize ?? row.BidSize,
        AskPrice = q.AskPrice ?? row.AskPrice,
        AskSize = q.AskSize ?? row.AskSize,
        LastPrice = q.LastPrice ?? row.LastPrice,
        MarkPrice = q.MarkPrice ?? row.MarkPrice,
        IndexPrice = q.IndexPrice ?? row.IndexPrice,
        FundingRate = q.FundingRate ?? row.FundingRate,
        OpenInterest = q.OpenInterest ?? row.OpenInterest,
        Turnover24h = q.Turnover24h ?? row.Turnover24h,
    };

    /// <summary>
    /// Every cell of band 1 as it should now read, addressed the way the markup addresses it.
    ///
    /// This walks the same columns the table renders and calls the same builders — see the class
    /// remarks on why that is not merely convenient.
    /// </summary>
    public static IReadOnlyList<LiveSlot> Slots(
        IReadOnlyList<VenueRowModel> rows,
        VerdictTable verdicts,
        IReadOnlyDictionary<int, HubQuote> quotes,
        DateTimeOffset now)
    {
        var lastTradeHasData = rows.Any(r => r.Row.LastTradeAt is not null);
        var slots = new List<LiveSlot>(rows.Count * 16);

        foreach (var r in rows)
        {
            var live = quotes.TryGetValue(r.Row.InstrumentId, out var q) && Wins(q, r.Row.ReceivedAt);
            var source = live ? "ws" : "latest";
            var liveAge = Format.SpacedAge(live ? (now - q!.At).TotalSeconds : null);
            var writtenAge = Format.SpacedAge(r.Row.ReceivedAt is { } w
                ? (now - new DateTimeOffset(DateTime.SpecifyKind(w, DateTimeKind.Utc))).TotalSeconds
                : null);

            foreach (var c in V2Columns.Visible(lastTradeHasData))
            {
                slots.Add(Slot(r, verdicts, c.Field, part: 0, source, liveAge, writtenAge, c.Field.ToString()));

                if (c.PairedField is { } paired)
                {
                    // A paired cell holds two figures with two ranks of their own — sharing a cell
                    // was a layout decision, never a merge of the two measurements.
                    slots.Add(Slot(r, verdicts, paired, part: 1, source, liveAge, writtenAge, c.Field.ToString()));
                }
            }
        }

        return slots;
    }

    private static LiveSlot Slot(
        VenueRowModel r,
        VerdictTable verdicts,
        V2Field field,
        int part,
        string source,
        string liveAge,
        string writtenAge,
        string group)
    {
        var cell = V2Cells.Field(r, field);
        var column = V2Ranks.ColumnOf(field);
        var rank = column is { } rc ? verdicts.Of(r.Row.InstrumentId, rc) : Verdict.None;

        return new LiveSlot(
            r.Row.InstrumentId,
            group.ToLowerInvariant(),
            part,
            cell.Text,
            cell.Sub,
            rank == Verdict.None ? null : V2Ranks.ClassWord(field, rank),
            rank == Verdict.None ? null : V2Ranks.ToneWord(rank),
            liveAge,
            writtenAge,
            source);
    }

    /// <summary>
    /// What this viewer has not been told yet. An unchanged slot does not travel: at five frames a
    /// second, sending a whole table each time would be the same figures over and over and a
    /// rewrite of every cell in the reader's browser for the two that moved.
    /// </summary>
    public static IReadOnlyList<LiveSlot> Diff(
        IReadOnlyDictionary<(int, string, int), LiveSlot> sent,
        IReadOnlyList<LiveSlot> current)
    {
        var changed = new List<LiveSlot>();
        foreach (var slot in current)
        {
            if (!sent.TryGetValue(slot.Key, out var previous) || previous != slot)
            {
                changed.Add(slot);
            }
        }

        return changed;
    }
}

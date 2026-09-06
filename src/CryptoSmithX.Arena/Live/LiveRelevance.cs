namespace CryptoSmithX.Arena.Live;

/// <summary>
/// Which passes a pair page has to be redrawn for.
///
/// The channel carries every collector pass on every segment in the system — five venues today, and
/// six datasets each. A page comparing one pair across five venues would redraw on all of them if it
/// asked no questions, and most of those redraws would produce a byte-identical table. So two
/// questions are asked, and both are about honesty as much as about cost: a redraw that changes
/// nothing still replaces the reader's table under their cursor, and a page that redraws for a
/// candles pass is a page claiming its candles were redrawn, which they are not.
/// </summary>
public static class LiveRelevance
{
    /// <summary>
    /// Whether a pass of this dataset can change anything the pair table shows.
    ///
    /// Named one by one rather than as a deny-list, because a dataset added later must arrive as
    /// "not on the page until somebody says so" — the same argument 0025 makes for not writing
    /// <c>alter default privileges</c>.
    /// </summary>
    /// <param name="collector">
    /// The dataset code from the notification, or null. Null means the segment row or its policy
    /// matrix changed rather than a collector finishing — and that IS relevant here, because the
    /// freshness windows the whole page is drawn against come out of
    /// <c>segment_dataset.interval_s → dataset.default_interval_s</c>. An operator moving the depth
    /// interval from 60 s to 600 s changes what is late on this page without a single figure moving.
    /// </param>
    public static bool Redraws(string? collector) => collector switch
    {
        // Writes market_snapshot_latest: price, bid, ask, sizes, mark, index, funding, turnover,
        // and open interest inline. Twelve of the fourteen cells.
        "snapshot" => true,

        // Writes the six depth bands and depth_at — the other two cells, and the only place on this
        // page where a dash can appear at all (blueprint §10).
        "depth" => true,

        // Disabled on every venue today (0014 seeds it that way, because the ticker carries open
        // interest inline), but where an operator turns it on it writes the open-interest cell and
        // its own clock. Listed so that turning it on does not quietly stop updating the page.
        "open_interest" => true,

        // Listings, delistings and status changes: which venues are rows on this page at all, and
        // the halt / post_only tag beside the symbol.
        "discovery" => true,

        // Candles and the timeframes rolled up from them. The live path deliberately does not redraw
        // the chart panels — they are hourly bars, the chart library owns those nodes, and pulling
        // them out from under a reader who is panning one is a worse answer than a panel that is up
        // to an hour behind and says so in its header.
        "candles" => false,
        "rollup" => false,

        // The funding HISTORY table, which this page does not read: the rate it prints and the
        // interval beside it come from the snapshot row (0025 grants no funding_rate_history).
        "funding" => false,

        // A policy or segment change. See the parameter note.
        null => true,

        // An unrecognised dataset. Not redrawn, and this is the deliberate direction to be wrong in:
        // a new feed that should move this page is a line in this switch and a decision somebody
        // took, whereas a default of true means the next dataset added to the system silently starts
        // repainting a public page nobody was thinking about at the time.
        _ => false,
    };

    /// <summary>
    /// Whether this event should redraw a page showing <paramref name="segments"/>.
    ///
    /// A pass on a segment this page does not show is ignored outright — that is what keeps the cost
    /// of a live tab proportional to the page the reader is actually on rather than to the size of
    /// the system.
    /// </summary>
    public static bool Matters(LiveEvent e, IReadOnlySet<string> segments) =>
        e.Segment is { } segment && segments.Contains(segment) && Redraws(e.Collector);
}

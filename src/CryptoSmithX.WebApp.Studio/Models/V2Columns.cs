using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// Band 1 as a table: every field its own column, all of them visible at once, and the CALL that
/// wrote them named once in a coloured band across the columns it owns.
///
/// <b>Why the order is by call and not by question.</b> The band is the whole point — three sources
/// answer at three rates and break separately, and which one is behind a figure is the first thing
/// worth knowing about it. A band can only be drawn across CONSECUTIVE columns, so the columns are
/// grouped by their call. The seven questions have not gone anywhere; they are how the fields were
/// chosen, and they are still what the units under the headings say.
/// </summary>
/// <param name="Width">
/// The column's width in pixels, and fixed on purpose.
///
/// Content-sized tracks looked obvious and were wrong twice over: a column re-measured itself
/// whenever its text changed, so a hundred and ten ages ticking over re-laid the whole grid once a
/// second, and the widest number a venue happened to print decided the width for everyone. Fixed
/// widths are chosen for what the column HOLDS — a price is eight digits, an interval is "8 h", a
/// turnover is nine digits and a comma every three — and they do not move.
/// </param>
/// <param name="PairedField">
/// B1: a second field mirrored into the same cell rather than given its own column — bid/ask and
/// bid size/ask size are one question asked in two directions, and printing them as two columns
/// repeated that question across the table. The paired field keeps its own rank (both ends or
/// neither still applies per figure, computed exactly as it was when it had a column), it is only
/// the CELL that is shared. Every other field's ranking in <see cref="Verdicts"/> is untouched.
/// </param>
public sealed record V2Column(
    V2Field Field,
    string Label,
    CallTone Call,
    int Width,
    string? Cut = null,
    bool Spark = false,
    V2Field? PairedField = null);

public static class V2Columns
{
    public static IReadOnlyList<V2Column> All { get; } =
    [
        // ── The ticker call: one response carries all of these ──────────────────────────────
        // Bid and ask mirrored into one cell (B1) — see V2Column.PairedField. Ask keeps its own
        // rank; it no longer keeps its own column.
        new(V2Field.Bid, "Bid / Ask", CallTone.Ticker, 108, Spark: true, PairedField: V2Field.Ask),
        new(V2Field.Spread, "Spread bps", CallTone.Ticker, 80, Cut: "spread", Spark: true),
        new(V2Field.Last, "Last", CallTone.Ticker, 100, Cut: "price", Spark: true),
        new(V2Field.Mark, "Mark", CallTone.Ticker, 104),
        new(V2Field.Index, "Index", CallTone.Ticker, 104),
        new(V2Field.FundingPerDay, "Funding /day", CallTone.Ticker, 96, Cut: "funding", Spark: true),
        new(V2Field.FundingRate, "Venue rate", CallTone.Ticker, 96),
        new(V2Field.FundingInterval, "Interval", CallTone.Ticker, 64),
        new(V2Field.Turnover24h, "Turnover 24h", CallTone.Ticker, 116, Cut: "turnover"),
        // P0-5 (UX audit): the header names the window, the same way TURNOVER 24H does — the figure
        // is a rolling 24h sum (V2Store.StressAsync), not the "last hour" the column used to read.
        new(V2Field.LiquidationVolume, "Liquidations 24h", CallTone.Ticker, 116, Cut: "liquidations", Spark: true),
        new(V2Field.LiquidationUnit, "Unit", CallTone.Ticker, 60),
        new(V2Field.VenueClock, "Venue clock", CallTone.Ticker, 88),
        new(V2Field.LastTrade, "Last trade", CallTone.Ticker, 88),

        // ── The open-interest call: its own clock, its own cadence ──────────────────────────
        new(V2Field.OpenInterest, "Open interest", CallTone.OpenInterest, 124, Cut: "oi", Spark: true),
        new(V2Field.Multiplier, "Multiplier", CallTone.OpenInterest, 72),

        // ── The depth sweep: the slowest of the three, and the one that goes quiet first ────
        // Bid size / ask size mirrored the same way as bid/ask above.
        new(V2Field.BidSize, "Bid / Ask size", CallTone.Depth, 100, PairedField: V2Field.AskSize),
        new(V2Field.Depth10, "Depth 10bps", CallTone.Depth, 108),
        new(V2Field.Depth25, "Depth 25bps", CallTone.Depth, 108, Cut: "depth25", Spark: true),
        new(V2Field.Depth50, "Depth 50bps", CallTone.Depth, 108),
        new(V2Field.BookReach, "Book reach", CallTone.Depth, 100),
    ];

    /// <summary>The grid's tracks: the listing cell, then every column at the width its content
    /// actually needs. One string, built here, so the bands, the headings and every row are laid on
    /// the same tracks by construction rather than by three declarations agreeing.</summary>
    public static string Tracks { get; } =
        "150px " + string.Join(" ", All.Select(c => c.Width + "px"));

    /// <summary>The bands over the headings: one per call, each as wide as the columns it owns.
    /// Built from the list rather than written down, so a column moved between calls cannot leave a
    /// band claiming a figure it did not write.</summary>
    public static IReadOnlyList<(CallTone Call, string Label, int Span)> Bands { get; } =
        [.. All
            .GroupBy(c => c.Call)
            .Select(g => (g.Key, Label: g.Key switch
            {
                CallTone.Ticker => "Ticker call · one response",
                CallTone.OpenInterest => "OI call",
                _ => "Depth sweep",
            }, Span: g.Count()))];

    /// <summary>The hourly history behind a column, where one is kept.</summary>
    public static IReadOnlyList<double?> Series(VenueRowModel r, V2Column c) => c.Field switch
    {
        V2Field.Bid or V2Field.Ask or V2Field.Last => r.Candles.Closes,
        V2Field.Spread => r.Metrics.Spread,
        V2Field.FundingPerDay => r.Metrics.Funding,
        V2Field.OpenInterest => r.Metrics.OpenInterest,
        V2Field.Depth25 => r.Metrics.Depth25,
        V2Field.LiquidationVolume => r.Liquidations,
        _ => [],
    };

    /// <summary>The age this column carries: the age of the call that wrote it, and never the row's.
    /// A depth sweep four minutes old under a two-second price is the whole reason the ages are per
    /// call. Cadence rides along for <see cref="Data.Freshness.PastWindow"/>'s cadence floor — the
    /// call's own configured interval, not the window, which already has cadence baked in.</summary>
    public static (double? Age, double? Window, double? Cadence) Freshness(VenueRowModel r, V2Column c) => c.Call switch
    {
        CallTone.Depth => (r.Ages.DepthSeconds, r.Windows.DepthSeconds, r.Cadence.DepthSeconds),
        CallTone.OpenInterest => (r.Ages.OpenInterestSeconds, r.Windows.OpenInterestSeconds, r.Cadence.OpenInterestSeconds),
        _ => (r.Ages.PriceSeconds, r.Windows.PriceSeconds, r.Cadence.PriceSeconds),
    };
}

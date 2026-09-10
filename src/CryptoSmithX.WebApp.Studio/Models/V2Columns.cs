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
/// The width the column's FIGURE needs, in pixels, and fixed on purpose.
///
/// Content-sized tracks looked obvious and were wrong twice over: a column re-measured itself
/// whenever its text changed, so a hundred and ten ages ticking over re-laid the whole grid once a
/// second, and the widest number a venue happened to print decided the width for everyone. Fixed
/// widths are chosen for what the column HOLDS — a price is eight digits, an interval is "8 h", a
/// turnover is nine digits and a comma every three — and they do not move.
///
/// The figure is not everything a ranked column holds: one row in it also carries the MAX or MIN
/// mark in front of the number. That slot is added by <see cref="V2Columns.Tracks"/>, not written
/// into these numbers, so each one keeps saying the single thing it knows — how wide the figure is.
///
/// "What the column holds" has to mean the WHOLE market, not the asset that was open when the
/// number was picked. Measured on the live page: a figure is 8.4px per character in the body mono,
/// and BTC's mark price is fifteen characters — «77,355.00000000», because the decimals come from
/// the venue's own price step. Four price columns and the turnover were sized for a smaller market
/// than the one they are pointed at, so on BTC the digits ran out of their cells and onto the
/// neighbours. A width here is characters × 8.4 plus the cell's 17px of padding and border.
/// </param>
/// <param name="PairedField">
/// B1: a second field mirrored into the same cell rather than given its own column — bid/ask and
/// bid size/ask size are one question asked in two directions, and printing them as two columns
/// repeated that question across the table. The paired field keeps its own rank (both ends or
/// neither still applies per figure, computed exactly as it was when it had a column), it is only
/// the CELL that is shared. Every other field's ranking in <see cref="Verdicts"/> is untouched.
/// </param>
/// <param name="LabelBreak">
/// Prompt 2, U-6: the header's second line, after the noun — "SPREAD" over "BPS", never a wrap
/// the browser chose. Null for a header that reads in one line.
/// </param>
public sealed record V2Column(
    V2Field Field,
    string Label,
    CallTone Call,
    int Width,
    string? Cut = null,
    bool Spark = false,
    V2Field? PairedField = null,
    string? LabelBreak = null);

public static class V2Columns
{
    public static IReadOnlyList<V2Column> All { get; } =
    [
        // ── The ticker call: one response carries all of these ──────────────────────────────
        // Bid and ask mirrored into one cell (B1) — see V2Column.PairedField. Ask keeps its own
        // rank; it no longer keeps its own column.
        // 108 держало восемь знаков, а BTC печатает «77,342.000000» — тринадцать: 109px цифр.
        new(V2Field.Bid, "Bid / Ask", CallTone.Ticker, 128, Spark: true, PairedField: V2Field.Ask),
        new(V2Field.Spread, "Spread", CallTone.Ticker, 80, Cut: "spread", Spark: true, LabelBreak: "bps"),
        new(V2Field.Last, "Last", CallTone.Ticker, 128, Cut: "price", Spark: true),
        // Марк и индекс печатаются шагом ЦЕНЫ площадки, а он бывает восьмизначным после запятой:
        // «77,355.00000000» — пятнадцать знаков, 126px. 104 не держало и близко.
        new(V2Field.Mark, "Mark", CallTone.Ticker, 144),
        new(V2Field.Index, "Index", CallTone.Ticker, 144),
        new(V2Field.FundingPerDay, "Funding", CallTone.Ticker, 96, Cut: "funding", Spark: true, LabelBreak: "/day"),
        new(V2Field.FundingRate, "Venue rate", CallTone.Ticker, 96),
        // Не цифра, а подпись под ней: «next 18:00:00Z» переносится, и её вторая строка — девять
        // знаков мелкого моно, 49px — в 47px не влезала и текла на соседнюю колонку.
        new(V2Field.FundingInterval, "Interval", CallTone.Ticker, 68),
        // «1,435,887,165» у ENA — тринадцать знаков: девяти цифр, на которые эта колонка была
        // рассчитана, рынку хватает не всегда.
        new(V2Field.Turnover24h, "Turnover", CallTone.Ticker, 128, Cut: "turnover", LabelBreak: "24h"),
        // P0-5 (UX audit): the header names the window, the same way TURNOVER 24H does — the figure
        // is a rolling 24h sum (V2Store.StressAsync), not the "last hour" the column used to read.
        // Prompt 2.1, W-1: UNIT folded into this cell's own sub-line (V2Cells.cs), VENUE CLOCK
        // moved to band 5 as a CLOCK DRIFT row (Asset.cshtml) — a column that printed one
        // constant word, and one that printed a fact about the VENUE rather than the market, on
        // every row of a table about comparing markets.
        new(V2Field.LiquidationVolume, "Liquidations", CallTone.Ticker, 116, Cut: "liquidations", Spark: true, LabelBreak: "24h"),

        // Prompt 2.1, W-1: shown only while at least one row actually has a last-trade figure —
        // computed at render time from Model.Rows (see the view's own lastTradeHasData), because
        // this list is built once and cannot see the model itself.
        new(V2Field.LastTrade, "Last trade", CallTone.Ticker, 88),

        // ── The open-interest call: its own clock, its own cadence ──────────────────────────
        // Prompt 2.1, W-1: MULTIPLIER folded into the listing cell (×1, ×10 — _V2Table.cshtml),
        // the same figure this column repeated on every row.
        new(V2Field.OpenInterest, "Open", CallTone.OpenInterest, 124, Cut: "oi", Spark: true, LabelBreak: "interest"),

        // ── The depth sweep: the slowest of the three, and the one that goes quiet first ────
        // Bid size / ask size mirrored the same way as bid/ask above.
        new(V2Field.BidSize, "Bid / Ask size", CallTone.Depth, 100, PairedField: V2Field.AskSize),
        // Prompt 2.1, W-3: DEPTH 10BPS / DEPTH 50BPS fold into this column's own second sub-line
        // (V2Cells.FoldedDepth) — one live column instead of three, DEPTH 25BPS by default,
        // still the cut this header opens band 2/3 to.
        new(V2Field.Depth25, "Depth", CallTone.Depth, 116, Cut: "depth25", Spark: true, LabelBreak: "25bps"),
        new(V2Field.BookReach, "Book reach", CallTone.Depth, 100),
    ];

    /// <summary>
    /// The room the MAX/MIN mark takes in front of a figure: the chip itself plus the gap between
    /// it and the first digit.
    ///
    /// It is a track's business and not the chip's. A cell is a fixed track, so a mark that does
    /// not fit does not wrap and does not shrink — it overflows, and because the figures are
    /// right-aligned it overflows LEFT, onto the neighbouring column. That is exactly what shipped:
    /// TURNOVER 24H's own MAX chip stood on top of INTERVAL's "4 h" one column to its left.
    /// </summary>
    public const int MarkSlot = 25;

    /// <summary>Whether this column can ever print a mark — its own field is ranked, or the field
    /// mirrored into the same cell is. Read from <see cref="V2Ranks.ColumnOf"/> rather than listed
    /// again here, so a field that gains or loses a rank changes its track in the same edit.</summary>
    private static bool Ranked(V2Column c) =>
        V2Ranks.ColumnOf(c.Field) is not null
        || (c.PairedField is { } paired && V2Ranks.ColumnOf(paired) is not null);

    /// <summary>The grid's tracks: the listing cell, then every column at the width its content
    /// actually needs — the figure, plus the mark's slot wherever a mark can appear. One string,
    /// built here, so the bands, the headings and every row are laid on the same tracks by
    /// construction rather than by three declarations agreeing.</summary>
    public static string Tracks { get; } =
        "150px " + string.Join(" ", All.Select(c => c.Width + (Ranked(c) ? MarkSlot : 0) + "px"));

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

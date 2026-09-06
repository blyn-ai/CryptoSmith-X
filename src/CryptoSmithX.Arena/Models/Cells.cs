using System.Globalization;
using CryptoSmithX.Arena.Data;

namespace CryptoSmithX.Arena.Models;

/// <summary>Which ink a figure takes. Rule 8, and it is the whole product.</summary>
public enum FigureInk
{
    /// <summary>A measurement.</summary>
    Data,

    /// <summary>A measured zero. Dimmer than a figure, but it is an OBSERVATION: nothing was
    /// resting there and we looked.</summary>
    Zero,

    /// <summary>Not measured. The em dash, and its own ink, so it can never be mistaken for a
    /// number that happened to be small.</summary>
    Unmeasured
}

/// <summary>Which call wrote this figure — and therefore which of the three hues it wears.</summary>
public enum CallTone
{
    Ticker,
    OpenInterest,
    Depth
}

/// <summary>
/// The spread column's mark, which is the spread column's own words for a rank — and one word that
/// is not a rank at all.
///
/// <b>Tight and Wide</b> are BEST and WORST said in the vocabulary of a spread. <c>Tag.jsx</c> says
/// so in as many words ("WIDE takes the WORST chip, because on the spread column it is the same
/// verdict") and the rendered kit draws it that way. Both ends are marked or neither, exactly as
/// rule 7 asks.
///
/// <b>Crossed</b> is neither end and it is not silence. A book whose bid stands above its ask is a
/// real observation about that venue at that instant — 0001 writes it as it stands and
/// <see cref="PairVenueRow.SpreadBps"/> returns the negative number rather than clamping it — but it
/// is not a narrow spread, and the spread spec ranks low-is-best with no floor, so the most broken
/// quotation on the page was crowned TIGHT and pushed an ordinary two-dollar book onto WIDE. Two
/// false statements out of one row. Leaving the row in the ranking and merely flooring the figure at
/// zero was rejected: that is the page rewriting a measurement, which is the act the whole surface
/// is built to refuse, and it would still have handed the crossed book a tie for the best spread.
/// Dropping the row silently was rejected too: the reader would see a spread cell with no mark and
/// no reason, which is the same hole the degraded exclusion had to be given a sentence to close.
/// So the row leaves the comparison and says why, in the slot the rank would have taken.
/// </summary>
public enum SpreadBand
{
    None,
    Tight,
    Wide,
    Crossed
}

/// <summary>
/// One rendered cell of the comparison table, decided in C# and drawn by a uniform loop in Razor.
///
/// <b>Why the decisions are here and not in the view.</b> Blueprint §1 rejected the client-island
/// architecture in one sentence: the rules that decide where a dash goes and where a zero goes, what
/// fades and what does not, where a mark slot is reserved — "это и есть продукт", and they cannot
/// live where nothing in CI can see them. A Razor template with seventeen bespoke branches is the
/// same problem one layer up: it compiles, but nothing can test it and every column is a fresh
/// chance to get one of those rules wrong in one place only. So the rules are applied once, here,
/// against types, and the view walks a list.
/// </summary>
/// <param name="Label">The column's own name, for the cell title. The eyebrow above it says the
/// same word; the title repeats it because a hover has to be readable on its own.</param>
/// <param name="Second">The ask half of a depth band. Depth is two numbers and is printed as two
/// numbers — a single summed figure would hide the one-sided book the column exists to catch.</param>
/// <param name="Note">
/// The mark slot's own text, where a column has something to say there that is not a rank. Today
/// that is funding and only funding: the normalised per-day figure, labelled as normalised, beside
/// the interval it was normalised from.
/// </param>
/// <param name="Weight">The fade, already computed. See <see cref="Freshness.Weight"/>.</param>
/// <param name="AgeSeconds">
/// Handed to the view so it can hand it to the client. The client re-derives everything from
/// <see cref="InstantMs"/> and its own monotonic anchor; this is the value the SERVER computed, and
/// it is what the page says before a single line of script has run.
/// </param>
/// <param name="InstantMs">
/// The absolute instant of the call that wrote this figure, in Unix milliseconds. Absolute, never
/// an age, because the client corrects for its own clock skew against a server instant and cannot
/// do that with a number that has already been subtracted.
/// </param>
/// <param name="RankGroup">
/// The set of cells this cell's comparative claim — its chip, its bar, its mirrored bar — is made
/// across, from <see cref="Verdicts.RankGroup"/>. Null on the columns that make no such claim:
/// funding, last, mark and index.
///
/// Handed to the view for the same reason <see cref="InstantMs"/> is. The claim was computed once,
/// against the instant of the request, and the clock does not stop when the response is written; a
/// call under a marked cell can cross its twelfth window with the tab still open. The client cannot
/// rank and must not learn how, so what it is given is the grouping and one verb — retract. See the
/// withdrawal block in <c>wwwroot/arena-ages.js</c>.
/// </param>
public sealed record MetricCellModel(
    string Label,
    string Text,
    FigureInk Ink,
    string? Second,
    FigureInk SecondInk,
    string? Note,
    double Weight,
    Verdict Verdict,
    SpreadBand Band,
    string? SparkPath,
    CallTone SparkTone,
    bool SparkHot,
    string? BarWidth,
    string? MirrorBidWidth,
    string? MirrorAskWidth,
    string AgeText,
    bool AgePastWindow,
    bool AgeMissing,
    CallTone Tint,
    string Title,
    double? AgeSeconds,
    double? WindowSeconds,
    long? InstantMs,
    string? RankGroup);

/// <summary>
/// The fourteen metric cells of one venue row, in the column order the design system fixes.
///
/// <b>The order is not cosmetic and it is not free to change.</b> Each call's columns are
/// contiguous — turnover sits among the ticker fields because it arrives in the ticker response,
/// not beside open interest where the admin console had it — and that contiguity is the only reason
/// the three header bands can be drawn at all. Move one column and a band either lies about which
/// call wrote the figures under it or stops being a single stretch.
/// </summary>
public static class RowCells
{
    /// <summary>
    /// Rule 11's seven lines come from two stores, and this is where they meet.
    ///
    /// Bid, ask and last sit on ONE series — the hourly price closes — because the price feeds three
    /// columns rather than one; that is <see cref="CandleStore"/>. Spread, funding, open interest
    /// and depth 25bps have no price column behind them and read from <c>market_metric_hour</c>
    /// through <see cref="MetricHourStore"/>. Both are resolved onto the same window list, so the
    /// seven lines on a row describe the same twenty-five hours.
    ///
    /// Those four columns used to render with the line slot reserved and EMPTY, because 0025
    /// withheld <c>market_metric_hour</c> from <c>arena_reader</c> on the sentence "агрегаты,
    /// которых страница пары не показывает" — a claim about this page, written before rule 11 was
    /// corrected against the rendered one, and false: these are the four columns rule 11 names. An
    /// empty reserved slot is not a neutral outcome either. It is the same eleven pixels of nothing
    /// that mark and index show, and rule 11 gives mark and index no second dimension AT ALL, so
    /// the page was saying "this column has no history" in exactly the place it meant "this column
    /// has a history and I cannot read it". 0026 grants the table and carries the whole argument.
    /// </summary>
    public static IReadOnlyList<MetricCellModel> Build(
        VenueRowModel venue, VerdictTable verdicts, ColumnScales scales)
    {
        var r = venue.Row;
        var w = venue.Windows;
        var a = venue.Ages;

        // Decimals come from the venue's own ticks, never from a constant. price_step is the venue's
        // statement about how precisely it is willing to be quoted, and printing to it is printing
        // what was said — four fixed decimals would invent two digits on BTC and hide six on a
        // 0.00000001 tick.
        //
        // Both are read from Format rather than worked out here, because Verdicts ranks these
        // columns at the precision they are PRINTED at and a second copy of either line would let
        // the ranking drift a digit away from the display.
        var px = Format.PriceDecimals(r);
        var qd = Format.QuantityDecimals(r);

        var pricePath = Format.SparkPath(venue.Candles.Closes);

        // The other four of rule 11's seven series. Each is drawn in ITS OWN call's hue — rule 4,
        // colour means which call wrote the figure — so the spread and funding lines are ticker
        // magenta beside the price, open interest is green, and depth is bronze. Before these
        // existed every line on the page was magenta and the sentence CallBands.jsx makes about the
        // table ("the same three hues reappear on every bar, line and tick below") was true of the
        // bars and the washes and false of the lines.
        //
        // None of the four is `hot`. That is not an oversight and it is not a default: the rendered
        // ui_kit carries thirty-nine sparklines and uses --spark-ticker-hot on exactly one per row,
        // --spark-oi-hot and --spark-depth-hot on none. The full-strength ink is the loud note in a
        // column of lines, and rule 6's argument about acid applies to it — a page where every line
        // is at full strength has no emphasis left.
        var m = venue.Metrics;
        var spreadPath = Format.SparkPath(m.Spread);
        var fundingPath = Format.SparkPath(m.Funding);
        var oiPath = Format.SparkPath(m.OpenInterest);

        // Depth 25bps is rule 11's one column that carries a line AND a bar, so its line is drawn
        // at the height left over once the mirror has taken its four pixels. See
        // Format.SplitSparkHeight: two marks, one history slot, and a row that keeps its figures on
        // one line.
        var depthPath = Format.SparkPath(m.Depth25, height: Format.SplitSparkHeight);

        return
        [
            // ── Ticker call ──────────────────────────────────────────────────────────────────
            Figure("Bid", r.BidPrice, px, r, PairColumn.Bid, CallTone.Ticker,
                a.PriceSeconds, w.PriceSeconds, r.ReceivedAt, verdicts, spark: pricePath, sparkHot: true),

            Figure("Ask", r.AskPrice, px, r, PairColumn.Ask, CallTone.Ticker,
                a.PriceSeconds, w.PriceSeconds, r.ReceivedAt, verdicts, spark: pricePath),

            // The spread carries TIGHT and WIDE instead of BEST and WORST. Same verdict, the
            // column's own words for it — Tag.jsx says so in as many words ("WIDE takes the WORST
            // chip, because on the spread column it is the same verdict"), and the rendered page
            // draws it that way. Both ends are still marked, which is what rule 7 asks; only the
            // wording changes.
            Spread(r, verdicts, a.PriceSeconds, w.PriceSeconds, spreadPath),

            Size("Bid size", r.BidSize, r, qd, PairColumn.BidSize, a.PriceSeconds, w.PriceSeconds,
                verdicts, scales),

            Size("Ask size", r.AskSize, r, qd, PairColumn.AskSize, a.PriceSeconds, w.PriceSeconds,
                verdicts, scales),

            // Last carries the price line but no rank: rule 11 refuses it a comparison, because a
            // last trade is a moment on each venue rather than a quantity they are competing on.
            Figure("Last", r.LastPrice, px, r, column: null, CallTone.Ticker,
                a.PriceSeconds, w.PriceSeconds, r.ReceivedAt, verdicts, spark: pricePath),

            // Mark and index carry NEITHER a line nor a bar, and that is the third correction to
            // rule 11: they are quoted rather than accumulated, and a bar against other venues would
            // rank numbers that are not competing.
            Figure("Mark", r.MarkPrice, px, r, column: null, CallTone.Ticker,
                a.PriceSeconds, w.PriceSeconds, r.ReceivedAt, verdicts),

            Figure("Index", r.IndexPrice, px, r, column: null, CallTone.Ticker,
                a.PriceSeconds, w.PriceSeconds, r.ReceivedAt, verdicts),

            Funding(r, a.PriceSeconds, w.PriceSeconds, fundingPath),

            Bar("Turnover 24h", r.Turnover24h, 0, PairColumn.Turnover24h, CallTone.Ticker,
                a.PriceSeconds, w.PriceSeconds, r.ReceivedAt, verdicts, scales, r,
                rawTitle: null),

            // ── Open-interest call: its own clock, its own age, its own vertical wash ─────────
            OpenInterest(r, qd, a.OpenInterestSeconds, w.OpenInterestSeconds, verdicts, oiPath),

            // ── Depth sweep: three bands, each two numbers, on a third clock ──────────────────
            Depth("Depth 10bps", r.DepthBid10, r.DepthAsk10, PairColumn.Depth10, r, a.DepthSeconds,
                w.DepthSeconds, verdicts, scales),

            // The only cell on the page that holds two history marks. Rule 11: "depth 25bps carries
            // both, the mirrored bar for the two sides and the line for the hour."
            Depth("Depth 25bps", r.DepthBid25, r.DepthAsk25, PairColumn.Depth25, r, a.DepthSeconds,
                w.DepthSeconds, verdicts, scales, spark: depthPath),

            Depth("Depth 50bps", r.DepthBid50, r.DepthAsk50, PairColumn.Depth50, r, a.DepthSeconds,
                w.DepthSeconds, verdicts, scales)
        ];
    }

    /// <summary>
    /// The longest single figure printed in each metric column, in characters — the one number the
    /// stylesheet needs in order to fit a figure to a column whose width it will not widen.
    ///
    /// <b>Why the column and not the cell.</b> The size is the column's, not each figure's, because
    /// a column is the unit this page is read in: the whole argument for the table is comparing one
    /// quantity down four venues. Fitting each cell on its own would print 765,034,123,000 at 9.2px
    /// and 19,824,942,751,000 at 7.7px one above the other, in the same column, right-aligned — the
    /// digit stack the comparison is made on stops lining up, and type size, which means nothing
    /// here, starts looking like emphasis on the SMALLER of two numbers. One column, one size.
    ///
    /// <b>Characters, not pixels.</b> This class does not know what a column is wide, and must not
    /// learn: the seventeen track widths live in one custom property in <c>arena.css</c>, and the
    /// comment on it says why four copies are four chances to drift. The server counts glyphs; the
    /// sheet, which is the only place that knows the track, turns a count into a type size. Every
    /// character a figure can hold is one advance of DM Mono — digits, the group separator, the
    /// decimal point, a sign, the per-cent, and the em dash a missing figure prints — so the count
    /// is <see cref="string.Length"/> and there are no surrogate pairs to spoil it.
    ///
    /// A column of nothing but dashes counts 1, never 0: the sheet divides by this number.
    /// </summary>
    public static IReadOnlyList<int> FigureGlyphs(IReadOnlyList<IReadOnlyList<MetricCellModel>> rows)
    {
        var widest = new int[rows.Count == 0 ? 0 : rows.Max(cells => cells.Count)];

        foreach (var cells in rows)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                // Both figures of a depth cell, each on its own: the pair may break BETWEEN them
                // (see `.a-pair`), so what has to fit a column is the wider of the two, never their
                // sum. Counting the pair whole would shrink all three depth columns for a book that
                // prints perfectly well on two lines.
                widest[i] = Math.Max(widest[i], cells[i].Text.Length);
                if (cells[i].Second is { } second)
                {
                    widest[i] = Math.Max(widest[i], second.Length);
                }
            }
        }

        for (var i = 0; i < widest.Length; i++)
        {
            widest[i] = Math.Max(1, widest[i]);
        }

        return widest;
    }

    // ── builders ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A plain figure: a number, an optional hourly line, a rank where the column has one.
    ///
    /// <paramref name="column"/> is null for the three columns that carry no rank at all — last,
    /// mark and index. That is not "computed and suppressed": <see cref="PairColumn"/> has no member
    /// for them, so there is nothing to look up, and passing null here is the only way to say it.
    /// </summary>
    private static MetricCellModel Figure(
        string label, double? value, int decimals, PairVenueRow r, PairColumn? column, CallTone tone,
        double? age, double? window, DateTime? instant, VerdictTable verdicts,
        string? spark = null, bool sparkHot = false)
    {
        var verdict = column is { } c ? verdicts.Of(r.InstrumentId, c) : Verdict.None;
        return new MetricCellModel(
            label, Format.Num(value, decimals), Ink(value), null, FigureInk.Unmeasured, null,
            Freshness.Weight(age, window), verdict, SpreadBand.None,
            spark, tone, sparkHot, null, null, null,
            Format.Age(age, window), Freshness.PastWindow(age, window), age is null,
            tone, Title(label, tone, instant, window), age, window, Ms(instant),
            column is { } g ? Verdicts.RankGroup(r, g) : null);
    }

    private static FigureInk Ink(double? value) =>
        value is null || double.IsNaN(value.Value) ? FigureInk.Unmeasured
        : value.Value == 0 ? FigureInk.Zero
        : FigureInk.Data;

    private static long? Ms(DateTime? instant) =>
        instant is { } t
            ? new DateTimeOffset(t.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
                : t.ToUniversalTime()).ToUnixTimeMilliseconds()
            : null;

    /// <summary>
    /// The cell's hover text: which call wrote it, the absolute UTC instant it wrote it at, and the
    /// window that instant is being judged against.
    ///
    /// The window is in here because §4 of the blueprint asks for it by name, and because without it
    /// a △ is an accusation with no evidence: the reader can see that a figure is late but not what
    /// it is late for. A cell whose window is unknown says so rather than showing a number.
    /// </summary>
    private static string Title(string label, CallTone tone, DateTime? instant, double? window)
    {
        var call = tone switch
        {
            CallTone.OpenInterest => "open-interest call",
            CallTone.Depth => "depth sweep",
            _ => "ticker call"
        };

        var when = instant is null ? "never observed" : Format.Utc(instant);
        var win = window is { } s
            ? "window " + s.ToString("0", CultureInfo.InvariantCulture) + " s"
            : "window unknown — not graded";
        return $"{label} · {call} · {when} · {win}";
    }

    /// <summary>
    /// The spread: one figure, one hourly line, and the column's own word for its rank.
    ///
    /// <b>A crossed book leaves the comparison and says so.</b> The figure is still the negative
    /// number the venue's own quotation implies — 0001 writes a crossed book as it stands, and this
    /// page does not clamp measurements — but a book whose bid is above its ask is not the tightest
    /// spread on the page, and the spec ranks low-is-best with no floor, so it took TIGHT and pushed
    /// an ordinary book onto WIDE at the same time. <see cref="Verdicts.TakesPart"/> is where it
    /// leaves the ranking, which is why the exclusion also governs whether the OTHER end is marked
    /// at all; here it only has to be said out loud.
    /// </summary>
    private static MetricCellModel Spread(
        PairVenueRow r, VerdictTable verdicts, double? age, double? window, string? spark)
    {
        var bps = r.SpreadBps;
        var verdict = verdicts.Of(r.InstrumentId, PairColumn.SpreadBps);

        // Crossed is checked first and not as a fallback of the verdict switch. It cannot collide —
        // TakesPart has already kept a crossed row out of the candidate list, so `verdict` is None
        // here — and reading it in this order is what says which of the two facts is the reason.
        var band = Verdicts.Crossed(r)
            ? SpreadBand.Crossed
            : verdict switch
            {
                Verdict.Best => SpreadBand.Tight,
                Verdict.Worst => SpreadBand.Wide,
                _ => SpreadBand.None
            };

        return new MetricCellModel(
            "Spread bps", Format.Num(bps, Verdicts.SpreadDecimals), Ink(bps), null, FigureInk.Unmeasured, null,
            Freshness.Weight(age, window), Verdict.None, band,
            // Rule 11's hourly line, from market_metric_hour.spread_bps_avg. The ticker call wrote
            // the figure above it, so the line takes the ticker hue.
            spark, CallTone.Ticker, false, null, null, null,
            Format.Age(age, window), Freshness.PastWindow(age, window), age is null,
            CallTone.Ticker, Title("Spread bps", CallTone.Ticker, r.ReceivedAt, window), age, window,
            Ms(r.ReceivedAt), Verdicts.RankGroup(r, PairColumn.SpreadBps));
    }

    /// <summary>
    /// A size, in base-asset units.
    ///
    /// Multiplied through <c>contract_multiplier</c>, because one unit of quantity is not one coin —
    /// 1000PEPE and kPEPE both carry a multiplier of 1000 (0001) — and two venues quoting the same
    /// book in different contract sizes are not comparable until they are in the same unit. The
    /// venue's own untouched figure goes in the title, so nothing is hidden by the conversion.
    /// </summary>
    private static MetricCellModel Size(
        string label, double? raw, PairVenueRow r, int decimals, PairColumn column,
        double? age, double? window, VerdictTable verdicts, ColumnScales scales)
    {
        var value = raw is { } q && r.ContractMultiplier > 0 ? q * r.ContractMultiplier : raw;
        var max = scales.Of(r.InstrumentId, column);

        return new MetricCellModel(
            label, Format.Num(value, decimals), Ink(value), null, FigureInk.Unmeasured, null,
            Freshness.Weight(age, window), verdicts.Of(r.InstrumentId, column), SpreadBand.None,
            null, CallTone.Ticker, false,
            max is not null && value is not null ? Format.BarWidth(value, max) : null,
            null, null,
            Format.Age(age, window), Freshness.PastWindow(age, window), age is null,
            CallTone.Ticker,
            Title(label, CallTone.Ticker, r.ReceivedAt, window)
                + " · venue's own figure " + Format.Num(raw, 8) + " × " + Format.Num(r.ContractMultiplier, 0),
            age, window, Ms(r.ReceivedAt), Verdicts.RankGroup(r, column));
    }

    private static MetricCellModel Bar(
        string label, double? value, int decimals, PairColumn column, CallTone tone,
        double? age, double? window, DateTime? instant, VerdictTable verdicts, ColumnScales scales,
        PairVenueRow r, string? rawTitle)
    {
        var max = scales.Of(r.InstrumentId, column);
        return new MetricCellModel(
            label, Format.Num(value, decimals), Ink(value), null, FigureInk.Unmeasured, null,
            Freshness.Weight(age, window), verdicts.Of(r.InstrumentId, column), SpreadBand.None,
            null, tone, false,
            max is not null && value is not null ? Format.BarWidth(value, max) : null,
            null, null,
            Format.Age(age, window), Freshness.PastWindow(age, window), age is null,
            tone, Title(label, tone, instant, window) + (rawTitle is null ? "" : " · " + rawTitle),
            age, window, Ms(instant), Verdicts.RankGroup(r, column));
    }

    /// <summary>
    /// Funding: the venue's own rate, its interval, and the per-day figure labelled as normalised.
    ///
    /// <b>Not ranked, in any scope.</b> Which direction is good depends on which side of the trade
    /// the reader is on, and the page does not know that. And the payment interval differs INSIDE a
    /// single venue — weex carries 4 hourly instruments, 480 four-hourly and 539 eight-hourly — so
    /// even the sign of "better" is not shared down the column. A rate without its period is a claim
    /// without a unit, so the period travels with the figure and the normalised number says out loud
    /// that it is normalised. <see cref="Verdicts"/> leaves this column out of the enum entirely,
    /// rather than computing a rank and suppressing it.
    /// </summary>
    private static MetricCellModel Funding(PairVenueRow r, double? age, double? window, string? spark)
    {
        // The interval and the normalised figure, together, in the slot the mark would occupy.
        // Three decimals rather than the rate's four: this is a derived number and printing it to
        // the same precision as the venue's own would dress a division up as a measurement.
        var note = r.FundingRatePerDay is { } perDay && r.FundingIntervalHours > 0
            ? r.FundingIntervalHours.ToString(CultureInfo.InvariantCulture) + "H\u00b7"
              + Format.SignedPercent(perDay, 3) + "/DAY"
            : null;

        return new MetricCellModel(
            "Funding", Format.SignedPercent(r.FundingRate), Ink(r.FundingRate),
            null, FigureInk.Unmeasured, note,
            Freshness.Weight(age, window), Verdict.None, SpreadBand.None,
            // The hour's LAST rate, not an average — funding is a level, not a flow, and the
            // rollup takes the last observation for the same reason.
            spark, CallTone.Ticker, false, null, null, null,
            Format.Age(age, window), Freshness.PastWindow(age, window), age is null,
            CallTone.Ticker,
            Title("Funding", CallTone.Ticker, r.ReceivedAt, window)
                + " · paid every " + r.FundingIntervalHours.ToString(CultureInfo.InvariantCulture)
                + " h · not ranked: direction depends on the reader's side and the interval differs "
                + "between instruments on one venue",
            age, window, Ms(r.ReceivedAt), RankGroup: null);
    }

    /// <summary>
    /// Open interest, in base-asset units, on the open-interest call's own clock.
    ///
    /// It gets its own age even where the venue carries it inline in the snapshot ticker, because
    /// <c>open_interest_at</c> exists precisely so the difference is visible rather than averaged
    /// away — Binance fetches it per symbol on its own loop (0001), and on that venue the two ages
    /// in one row are genuinely different numbers.
    ///
    /// A line and no bar: rule 11 gives this column the hourly series and refuses it a bar. The
    /// series is <c>market_metric_hour.open_interest_last</c> and it is drawn in the open-interest
    /// hue, which is the only green line on the page and the only one that is not about price.
    /// </summary>
    private static MetricCellModel OpenInterest(
        PairVenueRow r, int decimals, double? age, double? window, VerdictTable verdicts, string? spark)
    {
        var value = r.OpenInterest is { } oi && r.ContractMultiplier > 0
            ? oi * r.ContractMultiplier
            : r.OpenInterest;

        return new MetricCellModel(
            "Open interest", Format.Num(value, decimals), Ink(value), null, FigureInk.Unmeasured, null,
            Freshness.Weight(age, window), verdicts.Of(r.InstrumentId, PairColumn.OpenInterest), SpreadBand.None,
            spark, CallTone.OpenInterest, false, null, null, null,
            Format.Age(age, window), Freshness.PastWindow(age, window), age is null,
            CallTone.OpenInterest,
            Title("Open interest", CallTone.OpenInterest, r.OpenInterestAt, window)
                + " · venue's own figure " + Format.Num(r.OpenInterest, 4)
                + " × " + Format.Num(r.ContractMultiplier, 0),
            age, window, Ms(r.OpenInterestAt), Verdicts.RankGroup(r, PairColumn.OpenInterest));
    }

    /// <summary>
    /// One depth band: bid and ask, printed as the two numbers they are, with a mirrored bar under
    /// them.
    ///
    /// <b>The mirror is drawn only when both sides were measured.</b> A missing side would draw as
    /// no bar on that half, which reads as an empty book on that side — the strongest claim this
    /// column can make, made by an absence of data. The two figures are still printed, each with its
    /// own ink, so the reader sees exactly what was and was not measured.
    /// </summary>
    private static MetricCellModel Depth(
        string label, double? bid, double? ask, PairColumn column, PairVenueRow r,
        double? age, double? window, VerdictTable verdicts, ColumnScales scales, string? spark = null)
    {
        var max = scales.Of(r.InstrumentId, column);
        var both = bid is not null && ask is not null && max is not null;

        return new MetricCellModel(
            label, Format.Num(bid, 0), Ink(bid), Format.Num(ask, 0), Ink(ask), null,
            Freshness.Weight(age, window), verdicts.Of(r.InstrumentId, column), SpreadBand.None,
            spark, CallTone.Depth, false, null,
            both ? Format.BarWidth(bid, max) : null,
            both ? Format.BarWidth(ask, max) : null,
            Format.Age(age, window), Freshness.PastWindow(age, window), age is null,
            CallTone.Depth,
            Title(label, CallTone.Depth, r.DepthAt, window)
                + " · bid / ask notional in " + r.QuoteAsset,
            age, window, Ms(r.DepthAt), Verdicts.RankGroup(r, column));
    }
}

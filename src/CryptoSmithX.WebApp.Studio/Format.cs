using System.Globalization;
using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio;

/// <summary>
/// View formatting for the public surface. The same shape and the same first rule as
/// <c>WebApp.Admin/Format.cs</c> — a missing value is always an em dash, never an invented number — with
/// the pieces this surface adds: the age line's wording, the fade, and the two log-scaled bars.
///
/// Everything here is culture-invariant. The UI is English (the design system's content rules say
/// so), and more to the point a figure whose thousands separator depends on the server's locale is a
/// figure that reads differently on two machines that hold the same data.
/// </summary>
public static class Format
{
    /// <summary>Not measured. Never printed for a measured zero — that is an observation and gets
    /// its own, dimmer ink.</summary>
    public const string Dash = "—";

    /// <summary>Past its window: the count is no longer being graded.</summary>
    public const string PastWindowMark = "△";

    public static string Num(double? value, int decimals = 2) =>
        value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? Dash
            : value.Value.ToString("N" + decimals, CultureInfo.InvariantCulture);

    /// <summary>A signed percentage, for funding. The sign is always shown: on a funding rate the
    /// difference between paying and being paid is the sign, and a bare number hides it.</summary>
    public static string SignedPercent(double? fraction, int decimals = 4)
    {
        if (fraction is null || double.IsNaN(fraction.Value) || double.IsInfinity(fraction.Value))
        {
            return Dash;
        }

        var pct = fraction.Value * 100.0;
        return (pct > 0 ? "+" : "") + pct.ToString("N" + decimals, CultureInfo.InvariantCulture) + "%";
    }

    public static string Utc(DateTime? t) =>
        t is null ? Dash : t.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    public static string UtcClock(DateTime? t) =>
        t is null ? Dash : t.Value.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>P0-2 (UX audit): a bar's own instant, named the way "last bar 09 Sep 12:00Z" needs
    /// it — day, month and minute, no year and no seconds, because the value this labels is a
    /// window's open time and the reader is comparing it to "now" printed the same width away.</summary>
    public static string UtcDayMonthClock(DateTime? t) =>
        t is null ? Dash : t.Value.ToUniversalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// How many decimals a price wants, taken from the venue's own tick.
    ///
    /// Read from <c>price_step</c> rather than fixed at four, because four is right for AR at 6.4180
    /// and wrong for BTC at 98,102.50 in both directions — it invents two digits the venue does not
    /// quote, and on a 0.00000001 tick it hides six the venue does. The tick is the venue's
    /// statement about how precisely it is willing to be quoted, and printing to it is printing
    /// what was said.
    /// </summary>
    public static int Decimals(double step, int fallback = 2, int cap = 8)
    {
        if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step))
        {
            return fallback;
        }

        for (var d = 0; d <= cap; d++)
        {
            // Scale and compare against the integer: at d decimals the step is representable, so
            // that is the precision the venue quotes in.
            var scaled = step * Math.Pow(10, d);

            // AND THE SCALED STEP HAS TO BE AT LEAST ONE UNIT. Without that second condition a tick
            // SMALLER than the tolerance satisfies the first one at d = 0 — 1e-10 is within 1e-9 of
            // zero — so the function answered "no decimals" for the finest ticks on the venue list,
            // which is the opposite of what it exists to do. The deployed page printed Kraken's
            // PF_PEPEUSD bid, ask, last and mark as `0`, and its candle panel's header as "0 – 0
            // USD", on a book quoting 0.0000036209. A measured figure printed as zero is worse than
            // the dash-for-zero lie this file opens by forbidding: a dash says nothing was measured
            // and this said the price was nothing.
            //
            // "Representable at d decimals" means the step lands on a whole number of the LAST
            // decimal place, so the whole number has to be one or more. Zero is not a step.
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9 && Math.Abs(Math.Round(scaled)) >= 1)
            {
                return d;
            }
        }

        return cap;
    }

    /// <summary>
    /// The decimals a price is printed to on this venue's row.
    ///
    /// It lives here rather than in the cell builder because two callers need the same answer and
    /// they must not each work it out: <c>RowCells</c> prints the figure, and <see cref="Data.Verdicts"/>
    /// ranks the figure AS PRINTED. Two copies of this line would let the ranking drift a digit away
    /// from the display, which is exactly the bug that puts BEST and WORST on two cells rendering
    /// the same characters.
    /// </summary>
    public static int PriceDecimals(PairVenueRow r) => Decimals(r.PriceStep, fallback: 4);

    /// <summary>
    /// The decimals a quantity is printed to on this venue's row.
    ///
    /// Quantities are shown in BASE units, so the tick is carried into base units too, or the
    /// decimals describe a different quantity from the figure above them. Capped at four: past that
    /// the column stops fitting and the extra digits are below any venue's own step. Same two
    /// callers as <see cref="PriceDecimals"/>, same reason.
    /// </summary>
    public static int QuantityDecimals(PairVenueRow r) =>
        Decimals(r.ContractMultiplier > 0 ? r.QtyStep * r.ContractMultiplier : r.QtyStep,
            fallback: 2, cap: 4);

    /// <summary>
    /// <b>THE ONE AGE TOKEN THIS SURFACE PRINTS, AND IT IS THREE CHARACTERS WIDE.</b>
    ///
    /// Three places print an age — the freshness strip's named calls, the strip's two ends, and the
    /// age line under every figure — and before this they printed four different lengths from one
    /// vocabulary (<c>—</c>, <c>7 s</c>, <c>77 s</c>, <c>99+ s</c>). In a fixed-pitch face a length
    /// is a width, so the venue cell's three items measured 21 to 58px each against 134px of room,
    /// the row wrapped to a second line the moment two of its three ages reached ten seconds, and
    /// every row below it moved. The box was not too small: the text was not designed.
    ///
    /// So it is designed. Two characters of figure and one of unit, right-aligned in a slot the
    /// stylesheet sizes in characters — <c>7s</c>, <c>77s</c>, <c>2m</c>, <c>9h</c>, <c>4d</c> — and
    /// the slack is leading spaces, which is what a duration field is supposed to look like. The
    /// unit rung changes rather than the width: a figure that would need three digits is stated in
    /// the next unit up instead, which is also why the old <c>99+</c> cap is gone. It existed only
    /// to stop the slot changing width (its own comment said so); the slot does that now, and a
    /// call 489 seconds behind reads <c>9m</c> rather than hiding behind a plus sign.
    ///
    /// <b>Rejected, and why.</b>
    /// <list type="bullet">
    ///   <item>Zero-padding to <c>07 s</c> — a leading zero on an ELAPSED count reads as a clock
    ///     field (Atlassian's date-and-time guidance says not to zero-pad an hour; Windows' own
    ///     locale formats split <c>s</c> from <c>ss</c> for exactly this). A duration is padded with
    ///     spaces, and spaces are what the slot supplies.</item>
    ///   <item>Keeping <c>99+ s</c> and widening the venue column — rule 12 fixes the table at
    ///     1836px, and <c>--a-cols</c> says in as many words that it gets no more widenings. Three
    ///     items of <c>label + " " + "99+ s"</c> need 165px in a 148px cell whatever the gaps are.
    ///     Arithmetic, not preference.</item>
    ///   <item>Keeping <c>99+ s</c> and fitting the type to the box the way <c>.a-fig</c> does —
    ///     that division lands at 7.5px, below the 8.5px these ages are set at, on every page rather
    ///     than on the two pairs whose figures genuinely cannot fit.</item>
    ///   <item>Dropping the unit and stating it once (<c>Price 14</c>) — on a page with a price
    ///     column, a bare number beside the word "Price" is a different claim.</item>
    ///   <item>Spelling the unit (<c>2 min</c>) — six characters where four fit, and the reason the
    ///     narrow form is admissible here is that this is a fixed field and not prose. Primer's
    ///     relative-time guidance argues against narrow forms in running text and ships
    ///     <c>format="narrow"</c> for space-constrained slots; the prose on this page keeps the long
    ///     words (see <c>Statement</c> and the strip's own hovers).</item>
    /// </list>
    /// </summary>
    public static string ShortAge(double? seconds)
    {
        if (seconds is not { } age)
        {
            return Dash;
        }

        // A venue clock running ahead of ours is not a negative age; it is a clock we do not own.
        // Kraken stamps received_at from its own clock (SnapshotCollector), so this happens.
        //
        // Held as a double and never as an int. A stopped feed's age is a difference between two
        // instants and nothing bounds it: (int)1e12 does not overflow into a large number, it
        // overflows into a WRONG one, and the wrong one prints as an ordinary age.
        var whole = Math.Round(Math.Max(age, 0));
        if (whole < 100)
        {
            return Rung(whole, "s");
        }

        // CEILING on every coarse rung, and it is not a rounding preference. Rounding a 149-second
        // age to "2m" states that the figure is fresher than it is, which is the one direction this
        // surface is never allowed to be wrong in; the ceiling is wrong in the direction that
        // under-claims. The seconds rung keeps Math.Round because half a second is not a claim
        // either way, and because it is the behaviour the tests already pin.
        //
        // The rungs are chosen so that each one's range fits two digits before the next begins, and
        // that is why weeks are in the ladder: without them a call ninety-nine days behind would
        // step straight to "1y", which over-states by nine months to save a rung nobody reads. This
        // is the ladder GitHub's own narrow relative-time renders (s, m, h, d, w, y), not one
        // invented here.
        var minutes = Math.Ceiling(whole / 60.0);
        if (minutes < 100)
        {
            return Rung(minutes, "m");
        }

        var hours = Math.Ceiling(whole / 3600.0);
        if (hours < 100)
        {
            return Rung(hours, "h");
        }

        var days = Math.Ceiling(whole / 86400.0);
        if (days < 100)
        {
            return Rung(days, "d");
        }

        var weeks = Math.Ceiling(whole / 604800.0);
        if (weeks < 100)
        {
            return Rung(weeks, "w");
        }

        // The last rung, and it is clamped rather than continued. Ninety-nine years is older than
        // the schema, so the clamp is unreachable rather than lossy, and a rung above it would be a
        // seventh unit nobody will ever read.
        return Rung(Math.Min(Math.Ceiling(whole / 31_557_600.0), 99), "y");
    }

    /// <summary>One rung of the ladder above: the figure, then its unit, with no space between
    /// them. The space is what does not fit — see the rejections on ShortAge.</summary>
    private static string Rung(double figure, string unit) =>
        figure.ToString("0", CultureInfo.InvariantCulture) + unit;

    /// <summary>
    /// The same ladder with a space before the unit — <c>13 s</c>, <c>2 m</c> — for the second
    /// asset page, whose etalon writes it that way.
    ///
    /// Not a formatting preference on either side. The first page's age lives in a 134px cell where
    /// the space is the two pixels that made the table breathe as counts crossed ten; the second
    /// page has no such measurement to keep. studio-ages.js carries the same split behind
    /// <c>data-age-space</c>, because it rewrites these tokens every tick and one of the two would
    /// otherwise win a second after the render.
    /// </summary>
    public static string SpacedAge(double? seconds)
    {
        var text = ShortAge(seconds);
        if (text == Dash)
        {
            return text;
        }

        return text[..^1] + " " + text[^1];
    }

    /// <summary>
    /// The age line's text: the token above with the tense on it, and past twelve windows the word
    /// instead of the count.
    ///
    /// Eight characters at its longest, and the longest is the word <c>degraded</c> rather than any
    /// age — <c>99y ago</c> is seven. <c>studio.css</c> reserves the slot from that count.
    /// </summary>
    public static string Age(double? seconds, double? windowSeconds)
    {
        if (seconds is null)
        {
            return Dash;
        }

        if (Freshness.Degraded(seconds.Value, windowSeconds))
        {
            return "degraded";
        }

        return ShortAge(seconds) + " ago";
    }

    /// <summary>The fade, rendered as a CSS number. Three decimals is finer than a display can show
    /// and keeps the markup from carrying seventeen digits per cell.</summary>
    public static string Weight(double? ageSeconds, double? windowSeconds) =>
        Freshness.Weight(ageSeconds, windowSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// A figure's share of the largest venue in its group, log-scaled, as a CSS width.
    ///
    /// Log because linear flattens a 60-unit book against a 3,200-unit one into nothing — the exact
    /// case the depth columns exist to show. The <c>+1</c> keeps the transform defined at zero, and
    /// zero is a figure that can genuinely appear here: a book with nothing resting inside a band is
    /// an observation, and it draws as no bar rather than as no cell.
    /// </summary>
    public static string BarWidth(double? value, double? max)
    {
        if (value is not { } v || max is not { } m || m <= 0 || v < 0)
        {
            return "0%";
        }

        var pct = Math.Min(100.0, Math.Log10(v + 1) / Math.Log10(m + 1) * 100.0);
        return pct.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// A figure's share of the largest venue in its group, LINEAR — band 2's per-cut NOW bars
    /// (Prompt 2, U-1), where the reader is comparing a handful of listings at once rather than
    /// hunting a thin book buried under a thick one, which is the case <see cref="BarWidth"/>'s
    /// log scale exists for. A straight share reads truer here: twice the open interest should
    /// draw twice the bar.
    /// </summary>
    public static string LinearBarWidth(double? value, double? max)
    {
        if (value is not { } v || max is not { } m || m <= 0 || v < 0)
        {
            return "0%";
        }

        var pct = Math.Min(100.0, v / m * 100.0);
        return pct.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// Half-width for a bar that diverges from a centre zero line — FUNDING's NOW bar (Prompt 2,
    /// U-2). Negative figures draw left, positive draw right, and both are measured against the
    /// same <paramref name="maxAbs"/> (the larger of the population's positive and negative
    /// extremes) so the two directions are comparable at a glance rather than each scaled to its
    /// own side's maximum.
    /// </summary>
    public static string DivergingBarWidth(double? value, double? maxAbs)
    {
        if (value is not { } v || maxAbs is not { } m || m <= 0)
        {
            return "0%";
        }

        var pct = Math.Min(50.0, Math.Abs(v) / m * 50.0);
        return pct.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// The sparkline's geometry, in the user units the path is computed in and the viewBox is
    /// written with.
    ///
    /// <b>These three numbers are also in studio.css as --spark-w, --spark-h and --mirror-h, and the
    /// duplication is not an accident.</b> The path is arithmetic and has to be done somewhere the
    /// compiler and xunit can see it; the layout is CSS. The one thing that must never drift is the
    /// viewBox against the box the stylesheet gives the element, because an SVG whose viewBox does
    /// not match its box does not overflow or clip — it silently rescales, and a sparkline drawn at
    /// the wrong scale still looks like a sparkline. They are constants here, and named after the
    /// tokens they mirror, so a change on either side is a change somebody has to make twice on
    /// purpose rather than once by accident.
    /// </summary>
    public const double SparkWidth = 60;

    /// <inheritdoc cref="SparkWidth"/>
    public const double SparkHeight = 11;

    /// <inheritdoc cref="SparkWidth"/>
    public const double MirrorHeight = 4;

    /// <summary>
    /// The height of the line in rule 11's one "both" column — depth 25bps, which carries a line
    /// for the hour AND the mirrored bar for the two sides.
    ///
    /// The two share ONE history slot rather than taking two, so that the cell holding both is
    /// exactly as tall as the cell holding one and the row keeps its single figure line. The line
    /// gives up the height the mirror needs. The rendered ui_kit stacks a full-height line above a
    /// full mirror instead and its depth 25bps figure sits four and a half pixels above the rest of
    /// its row because of it — the kit is the authority on what a cell contains, not on a rhythm
    /// its own MetricCell says in a comment that it exists to keep.
    /// </summary>
    public const double SplitSparkHeight = SparkHeight - MirrorHeight;

    /// <summary>
    /// An SVG path for one sparkline, with the gaps left as gaps.
    ///
    /// <paramref name="values"/> is index-aligned with the page's hourly windows and holds null
    /// where that venue has no bar. The path starts a new subpath at every gap instead of joining
    /// across it: a venue that went dark for six hours must not draw an unbroken line through the
    /// hours it was silent. Null when fewer than two ADJACENT points exist anywhere in the series —
    /// there is no line to draw, and a single dot would be a history of one moment.
    /// </summary>
    /// <summary>
    /// Where point number <paramref name="index"/> of a series of <paramref name="count"/> points
    /// stands on a sparkline <paramref name="width"/> units wide.
    ///
    /// Public because the line is not the only thing drawn on that axis: the un-held head of a
    /// series is hatched behind it, and the hatch has to end exactly where the line begins. That
    /// was computed separately as index/count of the width — a near-miss that always fell a few
    /// units short of the first point, because the line's own step is width/(count-1), not
    /// width/count. One function, so the two can no longer disagree by construction.
    /// </summary>
    public static double SparkX(int index, int count, double width = SparkWidth)
        => count < 2 ? 0 : index * ((width - 1) / (count - 1)) + 0.5;

    public static string? SparkPath(
        IReadOnlyList<double?> values, double width = SparkWidth, double height = SparkHeight)
    {
        var present = values.Where(v => v is { } x && !double.IsNaN(x)).Select(v => v!.Value).ToList();
        return present.Count < 2 ? null : SparkPathScaled(values, present.Min(), present.Max(), width, height);
    }

    /// <summary>
    /// The same line, against a Y range the caller supplies rather than this series' own min/max.
    ///
    /// Prompt 2, U-2: FUNDING's band-3 card draws a zero baseline with a range symmetric around
    /// zero (<c>lo == -hi</c>) shared across every card in the cut, so a venue whose funding never
    /// left ±0.01 %/d is not stretched to fill the same height as one that swung to ±0.05 %/d, and
    /// zero always lands at the vertical centre of the plot by construction — <c>(0 - lo)/(hi -
    /// lo) == 0.5</c> whenever the range is symmetric — so a caller never computes that pixel by
    /// hand.
    /// </summary>
    public static string? SparkPathScaled(
        IReadOnlyList<double?> values, double lo, double hi, double width = SparkWidth, double height = SparkHeight)
    {
        if (values.Count < 2)
        {
            return null;
        }

        var present = values.Where(v => v is { } x && !double.IsNaN(x)).Select(v => v!.Value).ToList();
        if (present.Count < 2)
        {
            return null;
        }

        if (hi <= lo)
        {
            // A flat series is a real answer — the price did not move — so it draws as a flat line
            // rather than as nothing. Widening the range by a hair is what puts it in the middle
            // instead of on an edge.
            var pad = Math.Abs(hi) * 0.02;
            hi += pad > 0 ? pad : 1;
            lo -= pad > 0 ? pad : 1;
        }

        var parts = new List<string>();
        var drawing = false;
        var segmentPoints = 0;

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v || double.IsNaN(v))
            {
                drawing = false;
                continue;
            }

            var x = SparkX(i, values.Count, width);
            var y = (height - 1.5) - ((v - lo) / (hi - lo)) * (height - 3);
            parts.Add((drawing ? "L" : "M")
                + x.ToString("0.#", CultureInfo.InvariantCulture) + " "
                + y.ToString("0.#", CultureInfo.InvariantCulture));
            segmentPoints = drawing ? segmentPoints + 1 : segmentPoints;
            drawing = true;
        }

        return segmentPoints > 0 ? string.Join(" ", parts) : null;
    }
}

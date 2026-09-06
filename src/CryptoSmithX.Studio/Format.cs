using System.Globalization;
using CryptoSmithX.Studio.Data;
using CryptoSmithX.Studio.Models;

namespace CryptoSmithX.Studio;

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
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9)
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
    /// The age line's text: whole seconds, and past twelve windows the word instead of the count.
    ///
    /// Capped at "99+ s ago" the way the design system's own AgeLine is, so the line never changes
    /// width and the figures above it stay on one line across the row. Between the cap and the word
    /// there is no lost information — the absolute instant is in the cell's title, and past one
    /// window the △ beside this says the number has stopped being graded anyway.
    /// </summary>
    public static string Age(double? seconds, double? windowSeconds)
    {
        if (seconds is not { } age)
        {
            return Dash;
        }

        if (Freshness.Degraded(age, windowSeconds))
        {
            return "degraded";
        }

        // A venue clock running ahead of ours is not a negative age; it is a clock we do not own.
        // Kraken stamps received_at from its own clock (SnapshotCollector), so this happens.
        var whole = (int)Math.Round(Math.Max(age, 0));
        return whole > 99 ? "99+ s ago" : whole.ToString(CultureInfo.InvariantCulture) + " s ago";
    }

    /// <summary>The same, without the trailing "ago" — for the freshness strip, where every age sits
    /// beside the name of the call that carries it and the scale above says the tense.</summary>
    public static string ShortAge(double? seconds)
    {
        if (seconds is not { } age)
        {
            return Dash;
        }

        var whole = (int)Math.Round(Math.Max(age, 0));
        return whole > 99 ? "99+ s" : whole.ToString(CultureInfo.InvariantCulture) + " s";
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
    public static string? SparkPath(
        IReadOnlyList<double?> values, double width = SparkWidth, double height = SparkHeight)
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

        var lo = present.Min();
        var hi = present.Max();
        if (hi <= lo)
        {
            // A flat series is a real answer — the price did not move — so it draws as a flat line
            // rather than as nothing. Widening the range by a hair is what puts it in the middle
            // instead of on an edge.
            var pad = Math.Abs(hi) * 0.02;
            hi += pad > 0 ? pad : 1;
            lo -= pad > 0 ? pad : 1;
        }

        var step = (width - 1) / (values.Count - 1);
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

            var x = i * step + 0.5;
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

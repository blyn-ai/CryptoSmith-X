using System.Globalization;
using System.Text;

namespace CryptoSmithX.WebApp.Admin.Data;

/// <summary>Turns a series of numbers into an SVG polyline points string, scaled to a viewbox.
/// Server-side, no chart library — the only graphics rule the design system allows.</summary>
public static class Spark
{
    public static string Points(IReadOnlyList<double> values, double w, double h, double pad = 1)
    {
        if (values.Count < 2)
        {
            return "";
        }

        var lo = values.Min();
        var hi = values.Max();
        var span = hi - lo;
        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            var x = i / (double)(values.Count - 1) * w;
            // y is inverted (SVG origin top-left); a flat series sits on the mid-line.
            var norm = span < 1e-9 ? 0.5 : (values[i] - lo) / span;
            var y = h - pad - (norm * (h - pad * 2));
            sb.Append(x.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("0.0", CultureInfo.InvariantCulture));
            if (i < values.Count - 1)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The same line against an EXPLICIT domain, for charts that are meant to be compared with each
    /// other. The overload above scales every series to its own extent, which is right for a lone
    /// sparkline and wrong the moment two of them sit side by side: a collector averaging 900 ms and
    /// one averaging 12 ms both fill their box and draw the same picture.
    ///
    /// Nulls are windows with no observation, and they are skipped rather than plotted. x still comes
    /// from the index, so a series that only starts halfway through the axis draws a shorter line in
    /// the right place instead of being stretched across the whole width.
    /// </summary>
    public static string Points(
        IReadOnlyList<double?> values, double w, double h, double lo, double hi, double pad = 1)
    {
        if (values.Count < 2)
        {
            return "";
        }

        var span = hi - lo;
        var sb = new StringBuilder();
        var written = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v)
            {
                continue;
            }

            var x = i / (double)(values.Count - 1) * w;
            // y is inverted (SVG origin top-left); a flat series sits on the mid-line.
            var norm = span < 1e-9 ? 0.5 : Math.Clamp((v - lo) / span, 0, 1);
            var y = h - pad - (norm * (h - pad * 2));
            if (written > 0)
            {
                sb.Append(' ');
            }

            sb.Append(x.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("0.0", CultureInfo.InvariantCulture));
            written++;
        }

        return written < 2 ? "" : sb.ToString();
    }

    /// <summary>Filled-area variant: the line plus a baseline back to the start, for the big chart.</summary>
    public static string Area(IReadOnlyList<double> values, double w, double h)
    {
        var line = Points(values, w, h);
        return line.Length == 0 ? "" : $"0,{h.ToString("0", CultureInfo.InvariantCulture)} {line} {w.ToString("0", CultureInfo.InvariantCulture)},{h.ToString("0", CultureInfo.InvariantCulture)}";
    }
}

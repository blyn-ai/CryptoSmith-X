using System.Globalization;
using System.Text;

namespace CryptoSmithX.WebApp.Data;

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

    /// <summary>Filled-area variant: the line plus a baseline back to the start, for the big chart.</summary>
    public static string Area(IReadOnlyList<double> values, double w, double h)
    {
        var line = Points(values, w, h);
        return line.Length == 0 ? "" : $"0,{h.ToString("0", CultureInfo.InvariantCulture)} {line} {w.ToString("0", CultureInfo.InvariantCulture)},{h.ToString("0", CultureInfo.InvariantCulture)}";
    }
}

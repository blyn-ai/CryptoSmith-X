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

// Candlestick body appended by the instrument page. Same philosophy as the rest of this
// file: geometry as strings, no chart library. Colour comes from CSS classes — the one
// legal use of green/red in the console (market direction, per the design system).
public static partial class SparkCandles
{
    /// <summary>SVG inner markup for OHLC candles: one wick line + one body rect per bar.</summary>
    public static string Render(IReadOnlyList<CryptoSmithX.WebApp.Models.CandlePoint> candles, double w, double h, double pad = 4)
    {
        if (candles.Count == 0)
        {
            return "";
        }

        var lo = candles.Min(c => c.Low);
        var hi = candles.Max(c => c.High);
        var span = hi - lo;
        if (span <= 0)
        {
            span = 1; // flat series: everything lands mid-height rather than dividing by zero
        }

        double Y(double v) => h - pad - (v - lo) / span * (h - pad * 2);
        var slot = w / candles.Count;
        var bodyW = Math.Max(1.0, Math.Min(9.0, slot * 0.62));

        var sb = new System.Text.StringBuilder(candles.Count * 96);
        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            var cx = slot * i + slot / 2;
            var cls = c.Close >= c.Open ? "candle-up" : "candle-down";
            var yTop = Y(Math.Max(c.Open, c.Close));
            var yBot = Y(Math.Min(c.Open, c.Close));
            var bodyH = Math.Max(1.0, yBot - yTop); // a doji still gets a visible sliver

            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"<line class=\"{cls}\" x1=\"{cx:0.##}\" y1=\"{Y(c.High):0.##}\" x2=\"{cx:0.##}\" y2=\"{Y(c.Low):0.##}\" stroke-width=\"1\"/>");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"<rect class=\"{cls}\" x=\"{cx - bodyW / 2:0.##}\" y=\"{yTop:0.##}\" width=\"{bodyW:0.##}\" height=\"{bodyH:0.##}\"/>");
        }

        return sb.ToString();
    }
}

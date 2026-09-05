using System.Globalization;
using CryptoSmithX.WebApp.Models;

namespace CryptoSmithX.WebApp.Data;

/// <summary>One drawn piece of a candle, already in viewBox units.</summary>
/// <param name="Kind">up / down — the only sanctioned use of green and red in this console, which
/// are reserved for market direction and never for status.</param>
/// <param name="Faded">The bar covers fewer minutes than the window claims, or nothing traded in
/// it. Drawn, because it happened; dimmed, because it does not mean what a full bar means.</param>
public sealed record CandleRect(double X, double Y, double W, double H, string Kind, bool Faded)
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public string Xs => F(X);
    public string Ys => F(Y);
    public string Ws => F(W);
    public string Hs => F(H);
}

/// <summary>
/// Candlestick geometry, computed in C# so the view stays declarative and so the price-to-pixel
/// mapping exists in exactly one place.
///
/// Everything is a rect and nothing is stroked. A stroked line inside a viewBox drawn with
/// preserveAspectRatio="none" — which is how this codebase's charts fill their container — has its
/// stroke width stretched with the box, so a one-pixel wick becomes a wedge at one width and
/// invisible at another. Rects scale honestly.
///
/// A missing window produces NOTHING. That is deliberate: the slot stays empty and the gap is
/// visible as a gap. Closing it up, or bridging it with a line, would draw continuity that was never
/// observed — and on this platform a missing window is usually the venue going dark, which is
/// exactly what the reader needs to see.
/// </summary>
public static class Candles
{
    public static IReadOnlyList<CandleRect> Rects(
        IReadOnlyList<PairCandle?> candles, double low, double high, double width, double height)
    {
        var span = high - low;
        if (candles.Count == 0 || span <= 0)
        {
            return [];
        }

        var slot = width / candles.Count;
        var body = Math.Max(slot * 0.62, 0.8);
        var wick = Math.Max(slot * 0.16, 0.5);
        var rects = new List<CandleRect>(candles.Count * 2);

        double Y(double price) => height - ((price - low) / span * height);

        for (var i = 0; i < candles.Count; i++)
        {
            if (candles[i] is not { } c)
            {
                continue;
            }

            var centre = (i * slot) + (slot / 2);
            var kind = c.Up ? "up" : "down";
            var faded = !c.Complete || !c.Traded;

            var top = Y(c.High);
            rects.Add(new CandleRect(centre - (wick / 2), top, wick, Math.Max(Y(c.Low) - top, 0.5), kind, faded));

            var bodyTop = Y(Math.Max(c.Open, c.Close));
            var bodyBottom = Y(Math.Min(c.Open, c.Close));
            // A doji would otherwise be zero pixels tall and simply vanish, which reads as a missing
            // bar rather than as an unchanged one.
            rects.Add(new CandleRect(centre - (body / 2), bodyTop, body, Math.Max(bodyBottom - bodyTop, 0.9), kind, faded));
        }

        return rects;
    }

    /// <summary>
    /// Volume as a strip beneath the prices, on its own scale. Scaled to the loudest window of THIS
    /// platform, because volume across venues differs by orders of magnitude and one shared scale
    /// would flatten every smaller venue to nothing — the levels are in the table above, this strip
    /// is for shape.
    /// </summary>
    public static IReadOnlyList<CandleRect> Volume(
        IReadOnlyList<PairCandle?> candles, double max, double width, double height)
    {
        if (candles.Count == 0 || max <= 0)
        {
            return [];
        }

        var slot = width / candles.Count;
        var body = Math.Max(slot * 0.62, 0.8);
        var rects = new List<CandleRect>(candles.Count);

        for (var i = 0; i < candles.Count; i++)
        {
            if (candles[i] is not { } c || c.Volume <= 0)
            {
                continue;
            }

            var h = Math.Max(c.Volume / max * height, 0.6);
            rects.Add(new CandleRect(
                (i * slot) + (slot / 2) - (body / 2), height - h, body, h,
                c.Up ? "up" : "down", !c.Complete));
        }

        return rects;
    }
}

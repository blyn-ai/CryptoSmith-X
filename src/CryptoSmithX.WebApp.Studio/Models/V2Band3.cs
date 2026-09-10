namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// B3's timeframe/series switch, factored out of Asset.cshtml so it is the SAME code whichever way
/// the page reaches it: the first paint (Asset.cshtml) and the AJAX swap
/// (PairsV2Controller.Asset's XMLHttpRequest branch, returning _V2Band3Head/_V2Band3Cuts) both call
/// these, and a formula written twice is a formula free to drift the first time one copy changes.
/// </summary>
public static class V2Band3
{
    /// <summary>The series band 3's panels actually draw for this row: the selected timeframe/series
    /// when one was asked for, or the row's own hourly-trade series (already loaded for band 1's
    /// sparklines) at the default.</summary>
    public static CandleSeries Of(PairPageModel model, VenueRowModel r) =>
        model.Band3Candles is { } b3 && b3.TryGetValue(r.Row.InstrumentId, out var s) ? s : r.Candles;

    /// <summary>Rows with at least two closed bars in the selected series — the same bar the panel
    /// itself refuses to draw a single point as a line.</summary>
    public static IReadOnlyList<VenueRowModel> Drawable(PairPageModel model) =>
        model.Rows.Where(r => Of(model, r).Present >= 2).ToList();

    /// <summary>One price scale per quote asset, computed over whichever rows are drawable in the
    /// selected series — never over every row on the page, which would include quotes this series
    /// has nothing to say about.</summary>
    public static IReadOnlyDictionary<string, (double Lo, double Hi)> PriceScale(PairPageModel model)
    {
        var drawable = Drawable(model);
        return drawable
            .GroupBy(r => r.Row.QuoteAsset, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (Lo: g.Min(r => Of(model, r).Low!.Value), Hi: g.Max(r => Of(model, r).High!.Value)),
                StringComparer.Ordinal);
    }

    public static string TfLabel(short minutes) => minutes switch
    {
        60 => "1h", 240 => "4h", 720 => "12h", 1440 => "1d", _ => minutes + "m",
    };
}

using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The seventh cut, and P0-3 (UX audit): band 1's LIQUIDATIONS column and this cut's "now" used to
/// read two different series — band 1 a rolling 1h sum (V2Store.StressAsync), this cut the last
/// CLOSED hour of VenueRowModel.Liquidations, the same series band 3 draws. Two windows on two
/// tables meant one venue could print two different liquidation figures a scroll apart with
/// nothing on the page to say they were answering different questions. Both now read the SAME
/// rolling 24h sum (StressRow, matching TURNOVER 24H's own window).
///
/// <b>Band 3 keeps its own series.</b> The hourly bucket array (VenueRowModel.Liquidations) is
/// still what the sparkline draws — a time series over the day is a different, legitimate question
/// from "how much in the last 24h", and turnover makes the same split (V2CutSource.None: no
/// sparkline at all for a single rolling sum).
///
/// <b>An unmeasured venue is not a quiet one.</b> No stress row for this instrument and a reported
/// zero must not arrive at the same figure, because they are not the same fact. Null in, null out —
/// a dash on the page, never a bar of length zero.
/// </summary>
public sealed class LiquidationCutTests
{
    private static readonly V2Cut Cut = V2Cuts.All.Single(c => c.Key == "liquidations");

    private static StressRow? Stress(double? volume) =>
        volume is { } v ? new StressRow(1, v, "USD", DateTime.UtcNow) : null;

    [Fact]
    public void The_picker_carries_it()
    {
        Assert.Equal("Liquidations", Cut.Name);
        Assert.Equal(V2CutSource.Liquidations, Cut.Source);
    }

    [Fact]
    public void Now_is_the_rolling_24h_sum_band_one_also_reads()
    {
        Assert.Equal(60d, V2Cuts.Now(Rows.At(Rows.Venue(1)), Cut, Stress(60)));
    }

    [Fact]
    public void A_venue_with_no_stress_row_has_no_figure()
    {
        Assert.Null(V2Cuts.Now(Rows.At(Rows.Venue(1)), Cut, null));
    }

    [Fact]
    public void A_reported_zero_is_a_figure()
    {
        // Ноль — измерение: за 24 часа не ликвидировали ничего. Не то же, что «не смотрели».
        Assert.Equal(0d, V2Cuts.Now(Rows.At(Rows.Venue(1)), Cut, Stress(0)));
    }

    [Fact]
    public void Band_three_still_draws_its_own_hourly_series_not_the_24h_sum()
    {
        var row = Rows.At(Rows.Venue(1), liquidations: [10, 20, 30]);
        Assert.Equal(row.Liquidations, V2Cuts.Series(row, Cut));
    }
}

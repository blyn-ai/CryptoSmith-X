using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The seventh cut, and the two things about it that are easy to get quietly wrong.
///
/// <b>Its "now" is not its total.</b> Every other cut takes its band-2 figure off the row — a bid,
/// a spread, an open interest — and liquidations have no such column: what the venue publishes is
/// an hourly aggregate, and the honest "now" is the last CLOSED hour of the very series band 3
/// draws. A sum over the day would rank venues by how long we have been collecting them, and a
/// current partial hour would rank them by what minute of the hour it happens to be.
///
/// <b>An unmeasured venue is not a quiet one.</b> Hyperliquid publishing no aggregate and Binance
/// publishing a zero must not arrive at the same figure, because they are not the same fact. Null
/// through the series, null out of Now — a dash on the page, never a bar of length zero.
/// </summary>
public sealed class LiquidationCutTests
{
    private static readonly V2Cut Cut = V2Cuts.All.Single(c => c.Key == "liquidations");

    private static VenueRowModel Row(params double?[] hours) =>
        Rows.At(Rows.Venue(1), liquidations: hours);

    [Fact]
    public void The_picker_carries_it()
    {
        Assert.Equal("Liquidations", Cut.Name);
        Assert.Equal(V2CutSource.Liquidations, Cut.Source);
    }

    [Fact]
    public void Now_is_the_last_hour_that_was_measured()
    {
        Assert.Equal(30d, V2Cuts.Now(Row(10, 20, 30), Cut));
    }

    [Fact]
    public void Now_skips_the_hours_that_were_not()
    {
        // Ряд обрывается там, где обрывается сбор, а не там, где кончаются ликвидации.
        Assert.Equal(20d, V2Cuts.Now(Row(10, 20, null), Cut));
    }

    [Fact]
    public void A_venue_that_publishes_none_has_no_figure()
    {
        Assert.Null(V2Cuts.Now(Row(), Cut));
        Assert.Null(V2Cuts.Now(Row(null, null), Cut));
    }

    [Fact]
    public void A_reported_zero_is_a_figure()
    {
        // Ноль — измерение: за этот час не ликвидировали ничего. Не то же, что «не смотрели».
        Assert.Equal(0d, V2Cuts.Now(Row(10, 0), Cut));
    }

    [Fact]
    public void Band_three_draws_the_same_series_band_two_ranks()
    {
        var row = Row(10, 20, 30);
        Assert.Equal(row.Liquidations, V2Cuts.Series(row, Cut));
    }
}

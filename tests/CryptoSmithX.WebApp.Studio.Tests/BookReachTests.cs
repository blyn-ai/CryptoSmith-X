using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// "Book reach" printed two impossible figures on one screen: ±108,734 bps on Kraken and ±0 bps on
/// Binance, both beside "25 levels a side".
///
/// 108,734 bps is ten times the price — somewhere an absolute price was taken for a basis-point
/// distance — and a zero reach with twenty-five levels standing in the book cannot happen at all.
/// Both were printed as measurements. It is now computed here, from the same frame band 2 draws,
/// against the mid: a distance we can check rather than a column we inherited.
/// </summary>
public sealed class BookReachTests
{
    private static BookFrame Frame(double[] bids, double[] asks) =>
        new(1, DateTime.UnixEpoch, (short)Math.Max(bids.Length, asks.Length),
            bids, bids.Select(_ => 1d).ToArray(), asks, asks.Select(_ => 1d).ToArray());

    private static V2Cell Reach(BookFrame? f) =>
        V2Cells.Field(Rows.At(Rows.Venue(1)), V2Field.BookReach, book: f);

    [Fact]
    public void It_is_measured_from_the_mid_to_the_farthest_level()
    {
        // mid 100, farthest bid 99 → 100 bps; farthest ask 102 → 200 bps. The larger side is the
        // reach, and both are printed under it.
        var cell = Reach(Frame([100, 99], [100, 102]));

        Assert.Equal("±200 bps", cell.Text);
        Assert.Equal("100 / 200", cell.Sub);
    }

    [Fact]
    public void A_frame_that_says_ten_times_the_price_is_not_printed()
    {
        // The old column's ±108,734 bps, reproduced: a level an order of magnitude off the mid is a
        // broken frame, not a deep book, and a shopfront does not get to print it as a measurement.
        Assert.Equal(V2Cell.None, Reach(Frame([100, 1], [100, 102])));
    }

    [Fact]
    public void No_book_is_a_dash_and_not_a_zero()
    {
        Assert.Equal(V2Cell.None, Reach(null));
        Assert.Equal(V2Cell.None, Reach(Frame([], [])));
    }

    [Fact]
    public void A_book_that_is_only_the_top_of_book_reaches_nowhere_and_says_so()
    {
        // One level a side: the farthest level IS the top, so the distance is zero — and zero here
        // is not an observation about depth (rule 8), it is the absence of one.
        Assert.Equal(V2Cell.None, Reach(Frame([100], [100])));
    }
}

using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The Book reach column on a venue that has no book.
///
/// It recomputed its figure from the stored levels and had no other source, so a vault venue —
/// whose reach is the point where its quote engine stops quoting at all — printed nothing but a
/// reference price, while <c>market_snapshot.book_reach_bid/ask</c> held the measurement the depth
/// collector had written. Found on production, in the one column of fifteen that stayed blank after
/// everything else filled.
/// </summary>
public sealed class ReachWithoutABookTests
{
    [Fact]
    public void With_no_book_the_figure_the_collector_wrote_is_the_figure_shown()
    {
        var cell = V2Cells.Reach(
            book: null, depthRef: 2.3871, priceDecimals: 4,
            storedBidBps: 670, storedAskBps: 447, marketModel: "oracle_vault");

        Assert.Equal("±670 bps", cell.Text);
        Assert.Contains("670 / 447", cell.Sub);
        Assert.Contains("ref 2.3871", cell.Sub);
    }

    [Fact]
    public void A_reach_past_the_book_ceiling_is_printed_when_it_was_measured_rather_than_inferred()
    {
        // The ceiling's own comment is about a BROKEN FRAME — no venue's book has ever reached half
        // the price. A quote venue's reach is not a level: measured on Avantis's own listings, nine
        // of fifty-one are past 5 000 bps and the widest is 9 609. That is a thin market asking for
        // almost the whole notional at that size, and it is a fact, not a corrupt frame.
        var cell = V2Cells.Reach(
            book: null, depthRef: 1, priceDecimals: 2,
            storedBidBps: 9_609, storedAskBps: 4_000, marketModel: "oracle_vault");

        Assert.Equal("±9,609 bps", cell.Text);
    }

    [Fact]
    public void A_venue_with_neither_a_book_nor_a_written_reach_still_says_nothing()
    {
        // Nothing changed for the venues this column was already blank for: the new source is read
        // when it exists, and absence stays absence.
        var cell = V2Cells.Reach(book: null, depthRef: 2.5, priceDecimals: 2);

        Assert.Equal("—", cell.Text);
        Assert.Equal("ref 2.50", cell.Sub);
    }

    [Fact]
    public void A_book_venue_whose_frame_went_stale_is_still_held_to_the_book_ceiling()
    {
        // A REGRESSION, and it reached production for one deploy. Keying the ceiling on which path
        // produced the figure let WEEX print ±72,893 bps the moment its depth aged out and the
        // stored reach was read back instead — 729% of the price, which is precisely the broken
        // frame the ceiling exists to catch. The test is the market MODEL, not the path.
        var cell = V2Cells.Reach(
            book: null, depthRef: 2.382, priceDecimals: 3,
            storedBidBps: 5_641, storedAskBps: 72_893, marketModel: "orderbook");

        Assert.Equal("—", cell.Text);
    }

    [Fact]
    public void An_unknown_model_is_treated_as_having_a_book_rather_than_as_not_having_one()
    {
        // Every venue wired before the column existed has a book, and an absent model is not a claim
        // that this one is different.
        var cell = V2Cells.Reach(
            book: null, depthRef: 1, priceDecimals: 2,
            storedBidBps: 9_000, storedAskBps: 9_000, marketModel: null);

        Assert.Equal("—", cell.Text);
    }

    [Fact]
    public void One_side_measured_and_the_other_not_is_not_half_a_reach()
    {
        // A reach is a statement about both directions. Printing the one side we have would read as
        // the whole span.
        var cell = V2Cells.Reach(
            book: null, depthRef: 2.5, priceDecimals: 2, storedBidBps: 300, storedAskBps: null,
            marketModel: "oracle_vault");

        Assert.Equal("—", cell.Text);
    }
}

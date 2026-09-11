using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// A market that is shut is not a market that went stale, and until 0044 the page could not tell
/// them apart at all: age was measured from <c>received_at</c> and nothing else, so a venue with
/// trading hours would arrive on Monday as the reddest thing on screen — every cell magenta, the
/// header counting every call late, the strip at the spent end — while the venue had done nothing
/// wrong and the collectors had missed nothing.
///
/// The rule is narrow on purpose: the age is still SHOWN, because the reader is owed the number and
/// "this figure is from Friday" is true and worth knowing. What stops is the JUDGEMENT.
/// </summary>
public sealed class ClosedMarketTests
{
    private const double Window = 30;

    [Fact]
    public void A_call_long_past_its_window_is_late_when_the_venue_says_nothing()
    {
        // Null is every venue but Avantis: an unknown session is not an excuse, and these rows are
        // judged exactly as they were before the column existed.
        Assert.True(Freshness.PastWindow(3600, Window, null, marketOpen: null));
    }

    [Fact]
    public void The_same_call_is_late_when_the_venue_says_the_market_is_open() =>
        Assert.True(Freshness.PastWindow(3600, Window, null, marketOpen: true));

    [Fact]
    public void The_same_call_is_NOT_late_when_the_venue_says_the_market_is_shut() =>
        // Nothing is due from a closed market, so nothing about it can be overdue.
        Assert.False(Freshness.PastWindow(3600, Window, null, marketOpen: false));

    [Fact]
    public void A_weekend_old_figure_is_still_not_late_while_the_market_is_shut() =>
        // Two and a half days: the whole point is that this stays quiet rather than degrading with
        // every hour the exchange is closed.
        Assert.False(Freshness.PastWindow(216_000, Window, null, marketOpen: false));

    [Fact]
    public void Closed_is_only_the_venues_explicit_no()
    {
        Assert.True(Freshness.Closed(false));
        Assert.False(Freshness.Closed(true));
        Assert.False(Freshness.Closed(null));
    }

    [Fact]
    public void The_strip_agrees_with_the_cell_about_a_closed_market()
    {
        // Three places judge the same call — the cell's colour, the header's count and the strip.
        // If they disagreed on a Monday the page would be arguing with itself.
        var open = Rows.At(Rows.Venue(1) with { MarketOpen = true }, price: 3600, window: Window);
        var shut = Rows.At(Rows.Venue(2) with { MarketOpen = false }, price: 3600, window: Window);

        Assert.True(StripModel.Build(open).Calls.Single(c => c.Label == "Price").PastWindow);
        Assert.False(StripModel.Build(shut).Calls.Single(c => c.Label == "Price").PastWindow);
    }

    [Fact]
    public void A_closed_row_is_not_counted_late_in_the_headline()
    {
        // The count a reader can check against the rows below it. A shut market contributing to it
        // would make the page's own summary the least trustworthy thing on the page.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1) with { MarketOpen = false }, price: 3600, window: Window),
        };

        Assert.Equal(0, Statement.Counts(rows).Late);
    }

    [Fact]
    public void The_age_is_still_shown_for_a_closed_market()
    {
        // Not judged is not the same as not measured. "From Friday" is true and worth printing;
        // what would be false is calling it a late call.
        var shut = Rows.At(Rows.Venue(1) with { MarketOpen = false }, price: 3600, window: Window);
        var call = StripModel.Build(shut).Calls.Single(c => c.Label == "Price");

        Assert.Equal(3600, call.AgeSeconds);
        Assert.False(call.PastWindow);
    }

    [Fact]
    public void A_bookless_market_is_named_by_its_model_and_not_by_its_venue()
    {
        // The page explains four empty columns from segment.market_model, never from a list of
        // venue names in a template — the next oracle-priced venue must need no edit here.
        Assert.Equal("oracle_vault", Rows.Venue(1, marketModel: "oracle_vault").MarketModel);
        Assert.Equal("orderbook", Rows.Venue(2).MarketModel);
    }
}

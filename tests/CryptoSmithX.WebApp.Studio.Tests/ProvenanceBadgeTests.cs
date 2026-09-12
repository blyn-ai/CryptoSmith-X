using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The badge that tells a reader a figure was computed from a quote curve or an oracle feed rather
/// than read off a resting book — Q10K, PYTH, CAP, QUOTE. Numbers went into the ordinary cells
/// weeks before their provenance was visible anywhere but a migration comment; this is the column
/// finally saying so.
///
/// Keyed on <c>market_model</c>, never on a venue's name — the same rule <see cref="Freshness.HasBook"/>
/// already follows, so the next oracle-priced venue needs no edit here.
/// </summary>
public sealed class ProvenanceBadgeTests
{
    private static VenueRowModel Vault(double? bid = 1, double? ask = 1, double? bidSize = 1,
        double? askSize = 1, double? depthBid25 = 1, double? depthAsk25 = 1) =>
        Rows.At(Rows.Venue(1, marketModel: "oracle_vault",
            bid: bid, ask: ask, bidSize: bidSize, askSize: askSize,
            depthBid25: depthBid25, depthAsk25: depthAsk25));

    [Fact]
    public void A_book_venues_bid_carries_no_badge_at_all()
    {
        var cell = V2Cells.Field(Rows.At(Rows.Venue(1, bid: 100)), V2Field.Bid);

        Assert.Null(cell.Badge);
    }

    [Fact]
    public void A_vault_venues_bid_and_ask_are_marked_as_a_ten_thousand_dollar_quote()
    {
        var row = Vault();

        Assert.Equal("Q10K", V2Cells.Field(row, V2Field.Bid).Badge);
        Assert.Equal("Q10K", V2Cells.Field(row, V2Field.Ask).Badge);
        Assert.Equal("Q10K", V2Cells.Field(row, V2Field.Spread).Badge);
    }

    [Fact]
    public void A_vault_venues_mark_and_index_are_marked_as_the_oracle_feed()
    {
        var row = Rows.At(Rows.Venue(1, marketModel: "oracle_vault") with
        {
            MarkPrice = 100,
            IndexPrice = 100,
        });

        Assert.Equal("PYTH", V2Cells.Field(row, V2Field.Mark).Badge);
        Assert.Equal("PYTH", V2Cells.Field(row, V2Field.Index).Badge);
    }

    [Fact]
    public void A_vault_venues_sizes_are_marked_as_capacity_not_resting_size()
    {
        var row = Vault();

        Assert.Equal("CAP", V2Cells.Field(row, V2Field.BidSize).Badge);
        Assert.Equal("CAP", V2Cells.Field(row, V2Field.AskSize).Badge);
    }

    [Fact]
    public void A_vault_venues_depth_and_reach_are_marked_as_the_quote_curve()
    {
        var row = Vault();

        Assert.Equal("QUOTE", V2Cells.Field(row, V2Field.Depth25).Badge);
    }

    [Fact]
    public void An_empty_cell_never_carries_a_badge_with_nothing_to_explain()
    {
        // A dash with a badge on it would say "this absence was measured a special way", which is
        // not a fact — an absence is an absence regardless of how it was looked for.
        var row = Rows.At(Rows.Venue(1, marketModel: "oracle_vault"));

        Assert.Null(V2Cells.Field(row, V2Field.Bid).Badge);
        Assert.Equal("—", V2Cells.Field(row, V2Field.Bid).Text);
    }

    [Fact]
    public void The_native_tape_figures_carry_no_badge_even_on_a_vault_venue()
    {
        // Last, turnover and liquidations come straight off the venue's own public trade tape —
        // not a different kind of figure from a book venue's, so no badge marks them as one.
        var stress = new StressRow(1, 6_638, "USD", DateTime.UnixEpoch);
        var row = Rows.At(Rows.Venue(1, marketModel: "oracle_vault") with { LastPrice = 100 });

        Assert.Null(V2Cells.Field(row, V2Field.Last).Badge);
        Assert.Null(V2Cells.Field(row, V2Field.LiquidationVolume, stress).Badge);
    }

    [Fact]
    public void Every_badge_carries_a_title_that_names_the_endpoint_and_the_method()
    {
        var row = Vault();

        var cell = V2Cells.Field(row, V2Field.Bid);

        Assert.NotNull(cell.BadgeTitle);
        Assert.Contains("/risk/v2/spread", cell.BadgeTitle);
    }
}

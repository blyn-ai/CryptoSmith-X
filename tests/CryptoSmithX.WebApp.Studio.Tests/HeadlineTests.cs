using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// What the collapsed row states, which until now was less than the page had already worked out.
///
/// <b>The column headed PRICE printed the spread.</b> A reader running an eye down it met "4.61"
/// and spent a moment believing ADA traded at 4.61. It also left the two ranks that column exists
/// for — best bid and worst ask, which can belong to the same venue at the same instant — with
/// nowhere to appear until somebody clicked.
///
/// <b>And a rank was a wash with no word.</b> Colour on this page means the call that wrote a
/// figure (rule 5: acid green is the open-interest call), so a green fill with no word says
/// something else entirely. The word is the statement; the fill is its ground.
/// </summary>
public sealed class HeadlineTests
{
    private static readonly V2Group Price = V2Groups.All.Single(g => g.Key == "price");
    private static readonly V2Group Liquidity = V2Groups.All.Single(g => g.Key == "liquidity");
    private static readonly V2Group Carry = V2Groups.All.Single(g => g.Key == "carry");

    private static (VenueRowModel A, VenueRowModel B, VerdictTable V) Two()
    {
        var a = Rows.At(Rows.Venue(1, bid: 100, ask: 101, depthBid25: 900, depthAsk25: 100));
        var b = Rows.At(Rows.Venue(2, bid: 99, ask: 100, depthBid25: 100, depthAsk25: 900));
        return (a, b, Verdicts.Compute([a, b]));
    }

    [Fact]
    public void Price_stands_large_and_the_spread_stands_under_it()
    {
        var (a, _, v) = Two();
        var head = V2Cells.Head(a, Price, v);

        Assert.Equal(2, head.Big.Count);
        Assert.Contains("100", head.Big[0].Text);
        Assert.Contains("bps", Assert.Single(head.Small).Text);
    }

    [Fact]
    public void One_venue_can_hold_the_best_bid_and_the_worst_ask()
    {
        // The whole reason both halves are printed and both are ranked: A quotes the highest bid
        // AND the highest ask, which is the best of one and the worst of the other.
        var (a, _, v) = Two();
        var head = V2Cells.Head(a, Price, v);

        Assert.Equal("best", head.Big[0].Mark!.Word);
        Assert.Equal("worst", head.Big[1].Mark!.Word);
    }

    [Fact]
    public void The_spread_says_tight_or_wide_and_never_best()
    {
        var (a, b, v) = Two();

        var words = new[] { a, b }
            .Select(r => V2Cells.Head(r, Price, v).Small[0].Mark?.Word)
            .Where(w => w is not null)
            .ToList();

        Assert.All(words, w => Assert.Contains(w, new[] { "tight", "wide" }));
    }

    [Fact]
    public void Depth_keeps_the_sum_large_because_a_sum_is_comparable_and_a_price_is_not()
    {
        var (a, _, v) = Two();
        var head = V2Cells.Head(a, Liquidity, v);

        Assert.Equal(Format.Num(1000, 0), Assert.Single(head.Big).Text);
        Assert.Equal(2, head.Small.Count);
    }

    [Fact]
    public void Each_side_of_the_book_ranks_apart_from_the_sum()
    {
        // Both venues hold 1000 in total, so the sum ranks neither — and the sides are opposites,
        // which is the lean the sum cannot show.
        var (a, _, v) = Two();
        var head = V2Cells.Head(a, Liquidity, v);

        Assert.Null(head.Big[0].Mark);
        Assert.Equal("best", head.Small[0].Mark!.Word);
        Assert.Equal("worst", head.Small[1].Mark!.Word);
    }

    [Fact]
    public void Carry_carries_no_rank_because_the_normalisation_is_ours()
    {
        var (a, _, v) = Two();
        var head = V2Cells.Head(a, Carry, v);

        Assert.All(head.Big.Concat(head.Small), p => Assert.Null(p.Mark));
    }

    [Fact]
    public void Trust_and_stress_keep_no_fields_at_all()
    {
        // Trust listed the same three ages the freshness strip already draws two columns to its
        // left, and stress listed a unit, which is a property of a number and not a second number.
        Assert.Empty(V2Groups.All.Single(g => g.Key == "trust").Fields);
        Assert.Empty(V2Groups.All.Single(g => g.Key == "stress").Fields);
    }
}

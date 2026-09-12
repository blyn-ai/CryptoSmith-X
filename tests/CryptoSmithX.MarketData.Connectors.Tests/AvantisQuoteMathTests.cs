using CryptoSmithX.MarketData.Connectors.Avantis;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The arithmetic behind Avantis's quote columns. A venue with no resting book still answers
/// "at what price would you fill me, this size, this side" — measured on mainnet, anonymously:
/// 0.0100% up to 10 ETH, 0.016952% long against 0.018355% short at 100 ETH, 26.0 bps at 5 000,
/// and a flat refusal (SM004) at 50 000. That is a quote curve, and these are the functions that
/// turn it into the figures the table's own columns hold.
///
/// Pure on purpose: the engine that calls the endpoint is thin, and everything that could be
/// wrong about a number is here, where it can be asserted without a network.
/// </summary>
public sealed class AvantisQuoteMathTests
{
    [Fact]
    public void The_standard_quote_is_a_notional_turned_into_base_units_by_the_live_price()
    {
        // 10 000 USD of ETH at 2 500 is four ETH. The column compares venues, so the SIZE has to be
        // the same economic size everywhere — a fixed base quantity would ask BTC for a hundred
        // times the money it asks of a memecoin.
        Assert.Equal(4d, AvantisQuoteMath.CoinSize(10_000, indexPrice: 2_500, multiplier: 1m)!.Value, 9);
    }

    [Fact]
    public void A_contract_multiplier_is_part_of_that_conversion_and_not_ignored()
    {
        // One unit of exposure is one base unit on Avantis (×1), but the conversion must not assume
        // it: the same column is filled by venues whose contract is ten of something.
        Assert.Equal(0.4d, AvantisQuoteMath.CoinSize(10_000, indexPrice: 2_500, multiplier: 10m)!.Value, 9);
    }

    [Fact]
    public void A_price_that_is_not_a_price_yields_no_size_rather_than_infinity()
    {
        // Division by a missing oracle is how a quote of "∞ ETH" gets sent to a venue.
        Assert.Null(AvantisQuoteMath.CoinSize(10_000, indexPrice: 0, multiplier: 1m));
        Assert.Null(AvantisQuoteMath.CoinSize(10_000, indexPrice: double.NaN, multiplier: 1m));
    }

    [Fact]
    public void The_two_sides_are_quoted_around_the_oracle_and_never_around_each_other()
    {
        // The venue prices against the oracle plus its own side-dependent cost. Bid is what a seller
        // gets, ask is what a buyer pays, and each carries ITS OWN measured spread — at 100 ETH the
        // two differ (0.016952 long against 0.018355 short) and that asymmetry is the skew.
        var (bid, ask) = AvantisQuoteMath.BidAsk(index: 2_500, spreadLongPct: 0.02, spreadShortPct: 0.04);

        Assert.Equal(2_500 * (1 - 0.0004), bid!.Value, 9);
        Assert.Equal(2_500 * (1 + 0.0002), ask!.Value, 9);
    }

    [Fact]
    public void The_spread_column_is_the_distance_between_those_two_quotes_in_bps()
    {
        // Not the venue's stated spread_p, and not one side doubled: the figure a reader compares
        // against a book venue's (ask − bid) / mid has to be computed the same way.
        var (bid, ask) = AvantisQuoteMath.BidAsk(index: 2_500, spreadLongPct: 0.02, spreadShortPct: 0.04);

        // NOT 6 bps, and the difference is the point: the two halves are measured against the
        // ORACLE, but a spread is quoted against the MID, and an asymmetric quote puts the mid
        // below the oracle. The figure has to be computed the way a book venue's is, or the column
        // would be comparing two different quantities down its own length.
        var mid = (ask!.Value + bid!.Value) / 2d;
        Assert.Equal((ask.Value - bid.Value) / mid * 10_000d, AvantisQuoteMath.Bps(bid, ask)!.Value, 9);
        Assert.Equal(6.0006d, AvantisQuoteMath.Bps(bid, ask)!.Value, 4);
    }

    [Fact]
    public void A_symmetric_quote_still_produces_the_sum_of_both_halves()
    {
        var (bid, ask) = AvantisQuoteMath.BidAsk(index: 100, spreadLongPct: 0.01, spreadShortPct: 0.01);

        Assert.Equal(2d, AvantisQuoteMath.Bps(bid, ask)!.Value, 6);
    }

    [Fact]
    public void A_side_the_engine_would_not_quote_leaves_that_side_absent_not_zero()
    {
        // SM004 and a 403 both mean "no quote". Zero would read as a free fill, which is the most
        // expensive lie this table could tell.
        Assert.Null(AvantisQuoteMath.BidAsk(index: 2_500, spreadLongPct: null, spreadShortPct: 0.04).Ask);
        Assert.Null(AvantisQuoteMath.BidAsk(index: 2_500, spreadLongPct: 0.02, spreadShortPct: null).Bid);
    }

    [Fact]
    public void The_depth_search_reaches_out_geometrically_and_keeps_the_size_that_still_fit()
    {
        // Depth here is "the largest size the venue still quotes at or under this cost". The search
        // reaches outward by a factor while it has no ceiling, then closes in; and the ANSWER is
        // always a size that was actually quoted — never the midpoint it stopped on, which may be
        // the first size that was too expensive.
        var s = AvantisQuoteMath.Bracket.Start(seed: 4d);

        s = s.Observe(probe: 4d, withinThreshold: true);       // fits, so reach outward
        Assert.Equal(16d, s.NextProbe, 9);

        s = s.Observe(probe: 16d, withinThreshold: false);     // too dear: now there is a bracket
        // Geometric midpoint of [4, 16] — not 10. The bracket spans a factor, and an arithmetic
        // midpoint would spend most of its steps in the top half of it.
        Assert.Equal(8d, s.NextProbe, 9);

        s = s.Observe(probe: 8d, withinThreshold: true);
        Assert.Equal(8d, s.Best!.Value, 9);
        Assert.Equal(Math.Sqrt(8d * 16d), s.NextProbe, 9);
    }

    [Fact]
    public void A_seed_that_is_already_too_dear_searches_downward_instead()
    {
        // The 10 000 USD standard size is not small for a thin market: the very first probe can be
        // past the threshold, and then the bracket has to open below it rather than above.
        var s = AvantisQuoteMath.Bracket.Start(seed: 4d).Observe(probe: 4d, withinThreshold: false);

        Assert.Equal(1d, s.NextProbe, 9);
        Assert.Null(s.Best);
    }

    [Fact]
    public void The_widest_real_answer_on_this_venue_still_converges_inside_the_step_bound()
    {
        // A REGRESSION, and it was found by running the search against the live venue rather than
        // by reading it. ETH's true reach is about $41M against a $10 000 seed — four thousand
        // times the opening size. While the search doubled, the step bound fired during the reach
        // phase and every answer came back as an exact power of two times the seed: a lower bound
        // wearing a measurement's clothes, and it looked entirely plausible in the output.
        const double seed = 1d;
        const double truth = 4096d;

        var s = AvantisQuoteMath.Bracket.Start(seed);
        var steps = 0;
        while (!s.Done && steps < 100)
        {
            s = s.Observe(s.NextProbe, withinThreshold: s.NextProbe <= truth);
            steps++;
        }

        Assert.True(s.Done);
        Assert.True(steps <= AvantisQuoteMath.Bracket.MaxSteps, $"took {steps} steps");
        Assert.NotNull(s.Best);
        // Converged onto the boundary from below, not stopped at some 2^n short of it.
        Assert.InRange(s.Best!.Value, truth * 0.98, truth);
    }

    [Fact]
    public void A_search_that_never_fits_reports_no_depth_rather_than_the_smallest_probe()
    {
        // A market that will not take even a dust size has no depth at that threshold. Reporting the
        // last probe would invent a size the venue never agreed to.
        var s = AvantisQuoteMath.Bracket.Start(seed: 4d);
        var steps = 0;
        while (!s.Done && steps < 50)
        {
            s = s.Observe(s.NextProbe, withinThreshold: false);
            steps++;
        }

        Assert.Null(s.Best);
        Assert.True(s.Done);
        // And it gives up early rather than halving all the way to the step bound: fifty-one pairs
        // times three thresholds times two sides is what makes those wasted round trips add up.
        Assert.True(steps < AvantisQuoteMath.Bracket.MaxSteps, $"took {steps} steps on a dead market");
    }

    [Fact]
    public void The_search_stops_once_the_bracket_is_tight_enough_to_stop_costing_requests()
    {
        // Every step is an HTTP round trip against a live venue. One percent of the answer is finer
        // than the figure is ever read to, and the bound is what keeps the sweep affordable:
        // measured, the endpoint answers in 0.19 s and takes 53.6 req/s without throttling.
        var s = AvantisQuoteMath.Bracket.Start(seed: 100d);
        var steps = 0;
        while (!s.Done && steps < 50)
        {
            s = s.Observe(s.NextProbe, withinThreshold: s.NextProbe <= 137d);
            steps++;
        }

        Assert.True(s.Done);
        Assert.True(steps <= AvantisQuoteMath.Bracket.MaxSteps, $"took {steps} steps");
        Assert.NotNull(s.Best);
        // Converged onto the true boundary from below, within the tolerance.
        Assert.InRange(s.Best!.Value, 137d * (1 - AvantisQuoteMath.Bracket.Tolerance * 2), 137d);
    }
}

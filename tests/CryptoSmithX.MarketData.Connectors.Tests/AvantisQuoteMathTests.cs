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

/// <summary>
/// The tape's own guards. Both were found by running against the live venue rather than by reading
/// the code, which is the only reason they are here rather than in production.
/// </summary>
public sealed class AvantisTapeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static AvTrade Trade(string hash, long ts, double price, double size, bool liq = false) =>
        new(Id: hash, Hash: hash, Timestamp: ts, Price: price, OpenPrice: price,
            PositionSize: size, Buy: true, IsLong: true, IsOpen: true, IsLiquidation: liq,
            TxnType: "OPEN");

    [Fact]
    public void A_trade_with_no_clock_is_dropped_rather_than_dated_to_1970()
    {
        // FOUND ON THE HOST. The collector failed with `no partition of relation "trade" found for
        // row`: an absent timestamp became the epoch, and the trade table is partitioned by event
        // time. Failing loudly was the good case — the bad one is a row landing in the earliest
        // partition that happens to exist, fifty-six years wrong and silent.
        var tape = new AvantisTape();

        var fresh = tape.Observe("ETH/USD", [Trade("0xa", 0, 2_500, 1_000)], Now);

        Assert.Empty(fresh);
        Assert.Null(tape.Last("ETH/USD"));
    }

    [Fact]
    public void The_same_trade_seen_twice_is_counted_once()
    {
        // The tape is re-read in full every minute and ten records span hours, so all but the newest
        // are always ones we have already counted. Without identity the turnover column would grow
        // by the whole tape every pass.
        var tape = new AvantisTape();
        var t = Trade("0xa", Now.ToUnixTimeSeconds() - 60, 2_500, 1_000);

        Assert.Single(tape.Observe("ETH/USD", [t], Now));
        Assert.Empty(tape.Observe("ETH/USD", [t], Now));
        Assert.Equal(1_000d, tape.Turnover("ETH/USD", Now)!.Value, 6);
    }

    [Fact]
    public void Liquidations_are_summed_apart_from_the_turnover_they_are_part_of()
    {
        var tape = new AvantisTape();
        tape.Observe("ETH/USD",
        [
            Trade("0xa", Now.ToUnixTimeSeconds() - 60, 2_500, 1_000),
            Trade("0xb", Now.ToUnixTimeSeconds() - 30, 2_510, 400, liq: true),
        ], Now);

        Assert.Equal(1_400d, tape.Turnover("ETH/USD", Now)!.Value, 6);
        Assert.Equal(400d, tape.Liquidations("ETH/USD", Now)!.Value, 6);
    }

    [Fact]
    public void A_trade_older_than_the_window_leaves_the_sum_but_the_last_price_stands()
    {
        // Turnover is a rolling day; "the last trade" has no window at all. A quiet market must not
        // lose its last price just because that price is more than a day old.
        var tape = new AvantisTape();
        tape.Observe("ETH/USD", [Trade("0xa", Now.AddHours(-30).ToUnixTimeSeconds(), 2_500, 1_000)], Now);

        Assert.Equal(0d, tape.Turnover("ETH/USD", Now)!.Value, 6);
        Assert.Equal(2_500d, tape.Last("ETH/USD")!.Value.Price, 6);
    }

    [Fact]
    public void A_print_from_outside_the_window_is_remembered_but_never_handed_on()
    {
        // FOUND ON THE HOST, and the epoch guard did not catch it. A pair the venue has DELISTED
        // still answers "recent trades" — with prints that can be months old. The trade table is
        // partitioned by event time and holds this month and next, so an August print failed the
        // whole batch: `no partition of relation "trade" found for row`.
        //
        // Cutting at the window is not a patch over the partitioning. Anything outside the rolling
        // day is outside every figure computed from it, so passing it on would store a row no
        // column ever reads.
        var tape = new AvantisTape();

        var fresh = tape.Observe(
            "DEAD/USD", [Trade("0xa", Now.AddDays(-40).ToUnixTimeSeconds(), 7, 100)], Now);

        Assert.Empty(fresh);
        // Remembered, though: the venue's last print is still the venue's last print.
        Assert.Equal(7d, tape.Last("DEAD/USD")!.Value.Price, 6);
    }

    [Fact]
    public void A_symbol_never_polled_reports_nothing_rather_than_a_zero_day()
    {
        // "Nothing traded" and "we have not looked" are different facts and only one belongs in a
        // column.
        Assert.Null(new AvantisTape().Turnover("ETH/USD", Now));
    }
}

/// <summary>
/// Traded candles built from the tape rather than fetched — the honest ceiling on candle history
/// for a venue whose own "recent trades" answers with ten prints, not a history endpoint.
/// </summary>
public sealed class AvantisTapeCandleTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static AvTrade Trade(string id, DateTimeOffset at, double price, double notional) =>
        new(Id: id, Hash: id, Timestamp: at.ToUnixTimeSeconds(), Price: price, OpenPrice: price,
            PositionSize: notional, Buy: true, IsLong: true, IsOpen: true, IsLiquidation: false,
            TxnType: "OPEN");

    [Fact]
    public void One_trade_makes_a_bar_whose_four_prices_are_all_that_one_print()
    {
        var tape = new AvantisTape();
        tape.Observe("ETH/USD", [Trade("a", Base, 2_500, 1_000)], Base.AddMinutes(1));

        var bar = Assert.Single(tape.Bars1m("ETH/USD", Base.AddMinutes(-1), Base.AddMinutes(1)));

        Assert.Equal(2_500d, bar.Open, 6);
        Assert.Equal(2_500d, bar.High, 6);
        Assert.Equal(2_500d, bar.Low, 6);
        Assert.Equal(2_500d, bar.Close, 6);
        Assert.Equal(1, bar.TradeCount);
        Assert.Equal(1_000d, bar.VolumeQuote, 6);
    }

    [Fact]
    public void Open_and_close_are_by_TIME_not_by_the_order_trades_arrived_in()
    {
        // The tape's own ten-deep endpoint has been observed to answer with a later trade ahead of
        // an earlier one in the same response, so a fold keyed on arrival order would print the
        // wrong open on roughly half of all two-trade minutes.
        var tape = new AvantisTape();
        var t0 = Base;
        var t1 = Base.AddSeconds(20);

        // Handed to Observe in REVERSE time order on purpose.
        tape.Observe("ETH/USD", [Trade("late", t1, 2_510, 100), Trade("early", t0, 2_490, 100)], Base.AddMinutes(1));

        var bar = Assert.Single(tape.Bars1m("ETH/USD", Base.AddMinutes(-1), Base.AddMinutes(1)));

        Assert.Equal(2_490d, bar.Open, 6);
        Assert.Equal(2_510d, bar.Close, 6);
        Assert.Equal(2_510d, bar.High, 6);
        Assert.Equal(2_490d, bar.Low, 6);
    }

    [Fact]
    public void Two_trades_in_different_minutes_make_two_bars_not_one()
    {
        var tape = new AvantisTape();
        tape.Observe("ETH/USD",
        [
            Trade("a", Base, 2_500, 100),
            Trade("b", Base.AddMinutes(3), 2_520, 100),
        ], Base.AddMinutes(4));

        var bars = tape.Bars1m("ETH/USD", Base.AddMinutes(-1), Base.AddMinutes(4));

        Assert.Equal(2, bars.Count);
    }

    [Fact]
    public void The_bar_still_forming_is_never_returned()
    {
        var tape = new AvantisTape();
        var now = Base.AddSeconds(20);
        tape.Observe("ETH/USD", [Trade("a", Base, 2_500, 100)], now);

        // `to` lands inside the same minute the trade printed in — that minute has not closed yet.
        var bars = tape.Bars1m("ETH/USD", Base.AddMinutes(-1), now);

        Assert.Empty(bars);
    }

    [Fact]
    public void A_symbol_with_no_trades_at_all_returns_no_bars_rather_than_throwing()
    {
        Assert.Empty(new AvantisTape().Bars1m("ETH/USD", Base, Base.AddHours(1)));
    }
}

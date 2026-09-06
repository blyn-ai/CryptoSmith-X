using System.Text.RegularExpressions;
using CryptoSmithX.Studio;
using CryptoSmithX.Studio.Data;
using CryptoSmithX.Studio.Models;

namespace CryptoSmithX.Studio.Tests;

/// <summary>
/// Rule 7 and its scope. The fold is what makes the scope necessary: BTC/USD as a heading means
/// Kraken's book in USD sitting beside WEEX's in USDT on the same page, and an acid-green BEST chip
/// over a comparison of two currencies is a false statement, not a footnote.
/// </summary>
public sealed class VerdictScopeTests
{
    [Fact]
    public void A_price_never_ranks_against_a_different_quote_asset()
    {
        // The USD book is nominally cheaper only because a dollar is not a tether. Ranking them
        // together would call one of them the best bid on the page.
        var rows = new[]
        {
            Rows.Venue(1, quote: "USD", bid: 99_500),
            Rows.Venue(2, quote: "USDT", bid: 100_000)
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.Bid));
    }

    [Fact]
    public void Inside_one_quote_asset_both_ends_are_marked()
    {
        var rows = new[]
        {
            Rows.Venue(1, quote: "USDT", bid: 100_000),
            Rows.Venue(2, quote: "USDT", bid: 99_900),
            Rows.Venue(3, quote: "USD", bid: 99_500)
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Bid));
        // The lone USD row has nothing to be ranked against and stays unmarked, rather than being
        // called the best USD bid on the strength of being the only one.
        Assert.Equal(Verdict.None, v.Of(3, PairColumn.Bid));
    }

    [Fact]
    public void The_direction_is_per_column()
    {
        var rows = new[]
        {
            Rows.Venue(1, bid: 100_000, ask: 100_010),
            Rows.Venue(2, bid: 99_900, ask: 100_050)
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));   // highest bid
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Ask));   // lowest ask
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Ask));
    }

    [Fact]
    public void The_spread_ranks_across_the_whole_pair_because_basis_points_carry_no_currency()
    {
        var rows = new[]
        {
            Rows.Venue(1, quote: "USD", bid: 99_990, ask: 100_010),   // 20 bps
            Rows.Venue(2, quote: "USDT", bid: 99_999, ask: 100_001)   // 2 bps
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.Best, v.Of(2, PairColumn.SpreadBps));
        Assert.Equal(Verdict.Worst, v.Of(1, PairColumn.SpreadBps));
    }

    [Fact]
    public void Sizes_and_open_interest_rank_across_the_pair_through_the_contract_multiplier()
    {
        // One unit of quantity is not one coin: 1000PEPE and kPEPE both carry a multiplier of 1000
        // (0001). Comparing the raw numbers would rank contract sizes rather than books — the venue
        // with the smaller contract would win every size column on the page.
        var rows = new[]
        {
            Rows.Venue(1, quote: "USD", multiplier: 1000, bidSize: 5, openInterest: 5),
            Rows.Venue(2, quote: "USDT", multiplier: 1, bidSize: 900, openInterest: 900)
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.BidSize));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.BidSize));
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.OpenInterest));
    }

    [Fact]
    public void Depth_and_turnover_stay_inside_one_quote_asset()
    {
        // Both are notional sums in the quote currency (0001 on depth_bid_10bps, on turnover_24h),
        // so they carry the currency with them.
        var rows = new[]
        {
            Rows.Venue(1, quote: "USD", turnover: 1_000_000, depthBid25: 100, depthAsk25: 100),
            Rows.Venue(2, quote: "USDT", turnover: 9_000_000, depthBid25: 900, depthAsk25: 900)
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Turnover24h));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.Turnover24h));
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Depth25));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.Depth25));
    }

    [Fact]
    public void A_half_measured_depth_band_is_not_a_smaller_book()
    {
        // One side missing is a partial measurement, not a thinner book. Summing it against a
        // complete one would rank a gap as a number and hand it the WORST chip.
        var rows = new[]
        {
            Rows.Venue(1, depthBid25: 500, depthAsk25: null),
            Rows.Venue(2, depthBid25: 400, depthAsk25: 400),
            Rows.Venue(3, depthBid25: 900, depthAsk25: 900)
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Depth25));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Depth25));
        Assert.Equal(Verdict.Best, v.Of(3, PairColumn.Depth25));
    }

    [Fact]
    public void An_unmeasured_figure_never_wins_and_never_loses()
    {
        var rows = new[]
        {
            Rows.Venue(1, bid: 100_000),
            Rows.Venue(2, bid: 99_000),
            Rows.Venue(3, bid: null)
        };

        Assert.Equal(Verdict.None, Verdicts.Compute(Rows.Live(rows)).Of(3, PairColumn.Bid));
    }

    [Fact]
    public void One_comparable_row_is_not_a_comparison()
    {
        var rows = new[] { Rows.Venue(1, bid: 100_000), Rows.Venue(2, bid: null) };
        Assert.Equal(0, Verdicts.Compute(Rows.Live(rows)).Count);
    }

    [Fact]
    public void Both_ends_or_neither_and_a_tie_is_neither()
    {
        // Two venues showing the same bid are not a best and a worst. Marking them would invent a
        // difference of zero.
        var rows = new[] { Rows.Venue(1, bid: 100_000), Rows.Venue(2, bid: 100_000) };
        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.Bid));
    }

    [Fact]
    public void A_tie_at_one_end_marks_every_row_holding_it()
    {
        var rows = new[]
        {
            Rows.Venue(1, bid: 100_000),
            Rows.Venue(2, bid: 100_000),
            Rows.Venue(3, bid: 99_000)
        };

        var v = Verdicts.Compute(Rows.Live(rows));
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Best, v.Of(2, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(3, PairColumn.Bid));
    }

    [Fact]
    public void Funding_is_not_a_ranked_column_at_all()
    {
        // Direction depends on which side of the trade the reader is on, and the intervals differ
        // inside a single venue — weex carries 4 hourly instruments, 480 four-hourly and 539
        // eight-hourly. The mock's legend does not name funding under Best/Worst either.
        Assert.DoesNotContain(Enum.GetNames<PairColumn>(), n => n.Contains("Funding", StringComparison.Ordinal));
    }

    [Fact]
    public void Last_mark_and_index_are_quoted_rather_than_competed_and_carry_no_rank()
    {
        var names = Enum.GetNames<PairColumn>();
        Assert.DoesNotContain("Last", names);
        Assert.DoesNotContain("MarkPrice", names);
        Assert.DoesNotContain("IndexPrice", names);
    }

    [Fact]
    public void The_normalised_funding_figure_is_the_rate_over_its_own_interval()
    {
        // Printed beside the interval and labelled as normalised; never ranked.
        var eightHourly = Rows.Venue(1, funding: 0.0001, fundingHours: 8);
        var hourly = Rows.Venue(2, funding: 0.0001, fundingHours: 1);
        Assert.Equal(0.0003, eightHourly.FundingRatePerDay!.Value, 10);
        Assert.Equal(0.0024, hourly.FundingRatePerDay!.Value, 10);
    }

    // ── Degraded rows do not take part in a verdict ─────────────────────────────────────────────
    // A verdict is a claim about the market NOW. `degraded` is this page's own word for a figure
    // that has stopped meaning anything, printed in its own notebar, so an acid-green BEST wash on
    // a cell whose age line says `degraded` is the page contradicting itself in one cell.

    [Fact]
    public void A_degraded_row_neither_wins_a_column_nor_hands_the_loss_to_anybody()
    {
        // Kraken frozen three days ago on a plausible last quote, the other two live. Before this
        // was fixed the frozen row took BEST on the bid it has not been able to update since.
        var live = new[] { Rows.At(Rows.Venue(1, bid: 100_000)), Rows.At(Rows.Venue(2, bid: 99_900)) };
        var frozen = Rows.At(Rows.Venue(3, bid: 200_000), price: TimeSpan.FromDays(3).TotalSeconds);

        var v = Verdicts.Compute([..live, frozen]);

        Assert.Equal(Verdict.None, v.Of(3, PairColumn.Bid));
        // And the ranking among the living is exactly what it would be if the dead row were not
        // there at all — it does not quietly demote the others by existing.
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Bid));
    }

    [Fact]
    public void Dropping_the_degraded_rows_can_leave_one_candidate_and_then_the_column_is_unmarked()
    {
        // Rule 7 is both ends or neither, and the exclusion runs BEFORE the count. One live row is
        // not a comparison, so the survivor is not crowned for having outlived the other.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 100_000)),
            Rows.At(Rows.Venue(2, bid: 99_900), price: TimeSpan.FromDays(3).TotalSeconds)
        };

        Assert.Equal(0, Verdicts.Compute(rows).Count);
    }

    [Fact]
    public void Degraded_is_decided_per_call_so_a_dead_depth_sweep_leaves_a_live_bid_alone()
    {
        // Three calls, three clocks — the house rule this whole surface is built on. A venue whose
        // depth sweep died yesterday still has a two-second bid, and that bid is still a fact about
        // the market now.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 100_000, depthBid25: 900, depthAsk25: 900),
                depth: TimeSpan.FromDays(1).TotalSeconds),
            Rows.At(Rows.Venue(2, bid: 99_900, depthBid25: 100, depthAsk25: 100))
        };

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Bid));
        // The depth column loses its only other candidate with row 1, so it goes unmarked.
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Depth25));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.Depth25));
    }

    [Fact]
    public void Open_interest_is_judged_on_its_own_clock_and_not_on_the_ticker_s()
    {
        // Binance fetches open interest per symbol on its own loop (0001), which is why
        // open_interest_at exists at all. A row whose price is seconds old can carry an
        // open-interest figure that has not been refreshed in days.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 100_000, openInterest: 9_000),
                openInterest: TimeSpan.FromDays(3).TotalSeconds),
            Rows.At(Rows.Venue(2, bid: 99_900, openInterest: 100)),
            Rows.At(Rows.Venue(3, bid: 99_800, openInterest: 200))
        };

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.OpenInterest));
        Assert.Equal(Verdict.Best, v.Of(3, PairColumn.OpenInterest));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.OpenInterest));
    }

    [Fact]
    public void A_figure_past_its_window_still_ranks_because_that_is_not_the_same_verdict()
    {
        // △ says the count has stopped being graded, not that the figure has stopped being true.
        // Excluding everything past one window would empty most columns on a venue whose depth pass
        // takes 361 s — the exact over-correction the freshness model exists to avoid.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 100_000), price: Rows.Window * 3),
            Rows.At(Rows.Venue(2, bid: 99_900), price: Rows.Window * 3)
        };

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Bid));
    }

    [Fact]
    public void A_call_with_no_stated_cadence_is_never_degraded_and_still_ranks()
    {
        // Not knowing how often we look is not knowing that the figure is dead. Refusing it a rank
        // on that basis would be the page inventing the window it exists to report.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 100_000), price: TimeSpan.FromDays(3).TotalSeconds, window: null),
            Rows.At(Rows.Venue(2, bid: 99_900), price: TimeSpan.FromDays(3).TotalSeconds, window: null)
        };

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Bid));
    }

    // ── The tie guard compares what the reader sees ─────────────────────────────────────────────

    [Fact]
    public void Two_figures_that_print_the_same_characters_are_a_tie_and_not_a_best_and_a_worst()
    {
        // price_step 0.1, so both of these render as "99,999.0". Compared as raw doubles they are
        // 0.08 apart, and the page put BEST on one and WORST on the other over a difference it
        // never showed the reader.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 99_999.04)),
            Rows.At(Rows.Venue(2, bid: 99_998.96))
        };

        Assert.Equal("99,999.0", Format.Num(99_999.04, 1));
        Assert.Equal("99,999.0", Format.Num(99_998.96, 1));

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.Bid));
    }

    [Fact]
    public void Two_figures_printing_alike_are_both_at_the_same_end_when_a_third_row_differs()
    {
        // The other half of the same rule: they do not vanish from the ranking, they share an end.
        // Before, the second of them was neither best nor worst — one of two identical-looking
        // cells wore the acid wash and the other wore nothing.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 99_999.04)),
            Rows.At(Rows.Venue(2, bid: 99_998.96)),
            Rows.At(Rows.Venue(3, bid: 99_000))
        };

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Best, v.Of(2, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(3, PairColumn.Bid));
    }

    [Fact]
    public void The_spread_is_ranked_at_the_three_decimals_the_cell_prints()
    {
        // Two books whose spreads differ in the fifth decimal of a basis point. Both cells read
        // "0.200"; TIGHT on one and WIDE on the other is the page showing a difference its own
        // display says is not there.
        var tight = Rows.Venue(1, bid: 99_999.0, ask: 100_001.0);
        var barelyWider = Rows.Venue(2, bid: 99_998.999, ask: 100_001.0);

        Assert.NotEqual(tight.SpreadBps, barelyWider.SpreadBps);
        Assert.Equal(
            Format.Num(tight.SpreadBps, Verdicts.SpreadDecimals),
            Format.Num(barelyWider.SpreadBps, Verdicts.SpreadDecimals));

        var v = Verdicts.Compute([Rows.At(tight), Rows.At(barelyWider)]);
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.SpreadBps));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.SpreadBps));
    }

    [Fact]
    public void A_depth_band_is_ranked_at_the_whole_units_its_two_sides_are_printed_in()
    {
        // Each SIDE is rounded before the sum, because each side is a printed number. Two rows
        // showing "1,000 / 1,000" are the same book as far as this page is able to say.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, depthBid25: 1_000.4, depthAsk25: 1_000.4)),
            Rows.At(Rows.Venue(2, depthBid25: 999.6, depthAsk25: 999.6))
        };

        Assert.Equal(Format.Num(1_000.4, 0), Format.Num(999.6, 0));

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.Depth25));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.Depth25));
    }

    [Fact]
    public void A_difference_the_page_does_show_is_still_ranked()
    {
        // The guard is not a licence to stop ranking. One printed step apart is a real difference.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, bid: 99_999.1)),
            Rows.At(Rows.Venue(2, bid: 99_999.0))
        };

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.Bid));
    }

    [Fact]
    public void A_crossed_book_is_reported_as_it_stands()
    {
        // 0001 writes a crossed book unchanged because it is a fact. Clamping it here would be the
        // page inventing a market state the collector explicitly refused to invent.
        var crossed = Rows.Venue(1, bid: 100_010, ask: 100_000);
        Assert.True(crossed.SpreadBps < 0);
    }

    // ── The bar is the same claim, made quietly ─────────────────────────────────────────────────
    // Scales.cs says it in its own words: "a turnover bar drawn 40% as long as its neighbour is the
    // same claim as a WORST chip, made quietly". The first repair of the chip did not reach the bar
    // — ColumnScales took rows with no ages at all — so for one render the page refused to say
    // "largest size here" in the loud channel and said it in the quiet one, about the same cell.

    private static readonly double Dead = TimeSpan.FromDays(3).TotalSeconds;

    private static IReadOnlyList<MetricCellModel> Cells(
        VenueRowModel of, IReadOnlyList<VenueRowModel> page) =>
        RowCells.Build(of, Verdicts.Compute(page), ColumnScales.Compute(page));

    private static MetricCellModel Cell(
        VenueRowModel of, IReadOnlyList<VenueRowModel> page, string label) =>
        Cells(of, page).Single(c => c.Label == label);

    [Fact]
    public void A_degraded_row_sets_no_column_maximum_and_draws_no_bar_of_its_own()
    {
        // The verifier's own seed. Kraken frozen three days on a bid size of 999 against a live 10
        // and 8: the dead row drew the longest bar in the column — ghosted, but full length, since
        // the fade is opacity and not width — beside a BEST chip on a bar a third as long. The bar
        // said kraken has the deepest size on the page; the chip said weex does.
        var kraken = Rows.At(Rows.Venue(1, bidSize: 999), price: Dead);
        var weex = Rows.At(Rows.Venue(2, bidSize: 10));
        var binance = Rows.At(Rows.Venue(3, bidSize: 8));
        var page = new[] { kraken, weex, binance };

        var scales = ColumnScales.Compute(page);
        Assert.Null(scales.Of(1, PairColumn.BidSize));
        Assert.Equal(10, scales.Of(2, PairColumn.BidSize));
        Assert.Equal(10, scales.Of(3, PairColumn.BidSize));

        Assert.Null(Cell(kraken, page, "Bid size").BarWidth);
        Assert.Equal("100%", Cell(weex, page, "Bid size").BarWidth);
    }

    [Fact]
    public void Whatever_governs_the_chip_governs_the_bar_on_the_same_cell()
    {
        // Not two rules that happen to agree today: ColumnScales asks Verdicts.TakesPart. This is
        // the assertion that fails if either side grows its own copy.
        var live = Rows.At(Rows.Venue(1, bidSize: 10, depthBid25: 100, depthAsk25: 100));
        var dead = Rows.At(Rows.Venue(2, bidSize: 999, depthBid25: 900, depthAsk25: 900), price: Dead);
        var third = Rows.At(Rows.Venue(3, bidSize: 8, depthBid25: 50, depthAsk25: 50));
        var page = new[] { live, dead, third };

        // Every cell the dead call wrote: no chip, no band, no bar. Selected by the call rather
        // than by name, so a column added to the ticker band is covered the day it is added.
        foreach (var cell in Cells(dead, page).Where(c => c.Tint == CallTone.Ticker))
        {
            Assert.Equal(Verdict.None, cell.Verdict);
            Assert.Equal(SpreadBand.None, cell.Band);
            Assert.Null(cell.BarWidth);
        }

        // And only the price call is dead on that row, so its depth band — written by a sweep that
        // has just landed — keeps both. Three calls, three clocks, in the bar as in the chip.
        Assert.Equal(Verdict.Best, Cell(dead, page, "Depth 25bps").Verdict);
        Assert.NotNull(Cell(dead, page, "Depth 25bps").MirrorBidWidth);
    }

    [Fact]
    public void Dropping_the_degraded_rows_can_leave_one_bar_and_then_the_column_draws_none()
    {
        // The bar's version of "both ends or neither": a bar at full width against itself says
        // "largest" about a group of one. Same guard the verdict has, one line above it in Scales.
        var page = new[]
        {
            Rows.At(Rows.Venue(1, bidSize: 10)),
            Rows.At(Rows.Venue(2, bidSize: 999), price: Dead)
        };

        var scales = ColumnScales.Compute(page);
        Assert.Null(scales.Of(1, PairColumn.BidSize));
        Assert.Null(scales.Of(2, PairColumn.BidSize));
    }

    [Fact]
    public void The_bar_scales_in_the_scope_the_chip_ranks_in()
    {
        // One source for the grouping, which is the point of Verdicts.Scope: turnover is notional
        // in the quote asset and scales inside it, sizes carry no currency and scale across the
        // page. A bar drawn against a different set from the chip beside it would be two
        // comparisons on one cell.
        var usd = Rows.At(Rows.Venue(1, quote: "USD", turnover: 1_000_000, bidSize: 5));
        var usdt = Rows.At(Rows.Venue(2, quote: "USDT", turnover: 9_000_000, bidSize: 900));
        var scales = ColumnScales.Compute([usd, usdt]);

        Assert.Null(scales.Of(1, PairColumn.Turnover24h));
        Assert.Null(scales.Of(2, PairColumn.Turnover24h));
        Assert.Equal(900, scales.Of(1, PairColumn.BidSize));
        Assert.Equal(900, scales.Of(2, PairColumn.BidSize));
    }

    // ── The clock keeps moving after the render ─────────────────────────────────────────────────
    // The verdict is a judgement against `now`, and `now` advances while the tab is open. The server
    // ranks the living; the client cannot rank and is not taught how — it is handed the GROUPING a
    // claim was made across and one verb, retract. See the argument in studio-ages.js.

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void The_group_a_claim_is_made_across_is_derived_from_the_scope_and_not_written_by_hand()
    {
        var usd = Rows.Venue(1, quote: "USD");
        var usdt = Rows.Venue(2, quote: "USDT");

        // Per quote asset: two rows quoting differently are two claims, never one.
        Assert.NotEqual(Verdicts.RankGroup(usd, PairColumn.Bid), Verdicts.RankGroup(usdt, PairColumn.Bid));

        // Whole pair: the quote is not in the key at all, because it is not in the comparison.
        Assert.Equal(
            Verdicts.RankGroup(usd, PairColumn.BidSize),
            Verdicts.RankGroup(usdt, PairColumn.BidSize));

        // And two columns are never one claim, however they are scoped.
        Assert.NotEqual(
            Verdicts.RankGroup(usd, PairColumn.Bid), Verdicts.RankGroup(usd, PairColumn.Ask));
    }

    [Fact]
    public void The_view_hands_the_client_the_grouping_and_the_client_uses_that_one()
    {
        // The hook, in both files. Without it the script finds no groups, silently withdraws
        // nothing, and the page goes back to wearing an acid-green BEST on a feed its own headline
        // has just pronounced dead — a failure that looks exactly like a healthy page.
        Assert.Contains("data-rank=\"@c.RankGroup\"", Read("_PairTable.cshtml"), StringComparison.Ordinal);
        Assert.Contains("data-rank", Read("studio-ages.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_can_take_a_claim_away_and_has_no_way_to_grant_one()
    {
        var js = Read("studio-ages.js");

        // It withdraws the three channels one claim is made in — the chip, the bar and the mirrored
        // bar — and names each of them, so a fourth channel added to the mark slot has to be
        // considered here rather than silently surviving the withdrawal.
        foreach (var claim in new[] { "a-tag--best", "a-tag--worst", "a-tag--tight", "a-bar", "a-mirror" })
        {
            Assert.Contains(claim, js, StringComparison.Ordinal);
        }

        // The funding note shares the mark slot and is not a rank: it is the venue's own interval
        // and a normalised figure, and it stays true however old the call gets.
        Assert.DoesNotContain("a-mark-note", js, StringComparison.Ordinal);

        // And the structural half of "cannot grant": ranking means comparing figures, and this file
        // never reads one. Every number in it is an instant or a window. The day it queries a-fig
        // is the day rule 7 exists in two languages.
        Assert.DoesNotContain("a-fig", js, StringComparison.Ordinal);
    }

    // ── The reader is told the rule ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_chip_no_longer_claims_to_have_ranked_the_row_it_left_out()
    {
        var view = Read("_PairTable.cshtml");

        // The sentence that was false: the degraded venue IS compared here — on the page, in the
        // column, with a figure printed — and a larger bid three rows up wore no chip.
        Assert.DoesNotContain(
            "Best in this column across the venues compared here", view, StringComparison.Ordinal);
        Assert.Contains("degraded is not ranked", view, StringComparison.Ordinal);

        // Every chip the table can draw, from one sentence: BEST, WORST, and the band element that
        // renders TIGHT or WIDE. That one had no hover at all, so the rule reached two chips of
        // four and the spread column — where a crossed book is loudest — disclosed nothing.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(view, "title=\"@RankTitle\\(").Count);
    }

    // ── A crossed book is not a tight one ───────────────────────────────────────────────────────

    [Fact]
    public void A_crossed_book_does_not_take_the_tight_chip_and_does_not_push_a_healthy_one_onto_wide()
    {
        // The verifier's own three venues. binance quotes 64,010 bid against a 64,000 ask, which is
        // impossible as a book: SpreadBps is −1.562, the spread spec ranks low-is-best with no
        // floor, and the most broken quotation on the page was crowned TIGHT. By taking the good
        // end it also pushed weex's perfectly ordinary two-dollar book onto WIDE. One broken row,
        // two false statements.
        var rows = Rows.Live(
            Rows.Venue(1, bid: 64_010, ask: 64_000),        // crossed
            Rows.Venue(2, bid: 63_999.9, ask: 64_000.1),    // 0.031 bps
            Rows.Venue(3, bid: 63_999, ask: 64_001));       // 0.312 bps

        var v = Verdicts.Compute(rows);

        Assert.Equal(Verdict.None, v.Of(1, PairColumn.SpreadBps));
        Assert.Equal(Verdict.Best, v.Of(2, PairColumn.SpreadBps));
        Assert.Equal(Verdict.Worst, v.Of(3, PairColumn.SpreadBps));
    }

    [Fact]
    public void The_crossed_row_still_prints_its_negative_spread_and_says_what_it_is()
    {
        // 0001 writes a crossed book as it stands, so nothing here clamps the figure. What changes
        // is the claim on top of it: not a rank, and not silence either.
        var rows = Rows.Live(
            Rows.Venue(1, bid: 64_010, ask: 64_000),
            Rows.Venue(2, bid: 63_999.9, ask: 64_000.1),
            Rows.Venue(3, bid: 63_999, ask: 64_001));

        var crossed = RowCells.Build(rows[0], Verdicts.Compute(rows), ColumnScales.Compute(rows))
            .Single(c => c.Label == "Spread bps");

        Assert.Equal(SpreadBand.Crossed, crossed.Band);
        Assert.StartsWith("-", crossed.Text, StringComparison.Ordinal);
        Assert.Equal(FigureInk.Data, crossed.Ink);

        // …and the two live rows still carry the column's own words for the rank.
        var tight = RowCells.Build(rows[1], Verdicts.Compute(rows), ColumnScales.Compute(rows))
            .Single(c => c.Label == "Spread bps");
        Assert.Equal(SpreadBand.Tight, tight.Band);
    }

    [Fact]
    public void Dropping_the_crossed_row_can_leave_one_book_and_then_the_column_goes_unmarked()
    {
        // "Both ends or neither" is applied AFTER the exclusion, the same way the degraded one is.
        // Two rows of which one is crossed are not two comparable books, and crowning the survivor
        // would be a ranking against nothing.
        var rows = Rows.Live(
            Rows.Venue(1, bid: 64_010, ask: 64_000),
            Rows.Venue(2, bid: 63_999, ask: 64_001));

        var v = Verdicts.Compute(rows);

        Assert.Equal(Verdict.None, v.Of(1, PairColumn.SpreadBps));
        Assert.Equal(Verdict.None, v.Of(2, PairColumn.SpreadBps));
    }

    [Fact]
    public void A_locked_book_is_not_crossed_and_wins_the_column_it_earned()
    {
        // Bid exactly at the ask is a zero-width book. Zero is an OBSERVATION on this surface, and
        // the tightest a spread can be — excluding it would be the dash-for-a-zero substitution the
        // whole page is built to refuse, in the other direction.
        var rows = Rows.Live(
            Rows.Venue(1, bid: 64_000, ask: 64_000),
            Rows.Venue(2, bid: 63_999, ask: 64_001));

        Assert.False(Verdicts.Crossed(rows[0].Row));

        var v = Verdicts.Compute(rows);
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.SpreadBps));
        Assert.Equal(Verdict.Worst, v.Of(2, PairColumn.SpreadBps));
    }

    [Fact]
    public void Only_the_spread_leaves_the_comparison_and_not_the_whole_row()
    {
        // Bid and ask are each still that venue's own quotation, and the highest bid on the page is
        // still the highest bid. What stops meaning anything is the DIFFERENCE between them, and
        // that is one column.
        var rows = Rows.Live(
            Rows.Venue(1, bid: 64_010, ask: 64_000, bidSize: 900),
            Rows.Venue(2, bid: 63_999, ask: 64_001, bidSize: 10));

        var v = Verdicts.Compute(rows);

        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.Bid));
        Assert.Equal(Verdict.Best, v.Of(1, PairColumn.BidSize));
        Assert.Equal(Verdict.None, v.Of(1, PairColumn.SpreadBps));
    }

    [Fact]
    public void The_crossed_chip_is_not_a_rank_and_the_client_never_withdraws_it()
    {
        // It is a fact about that venue's own two figures rather than a comparison across venues,
        // so it stays true however old the call gets — exactly like the funding note, and for the
        // same reason the withdrawal names the three rank classes instead of emptying the slot.
        var script = Read("studio-ages.js");
        Assert.DoesNotContain("a-tag--crossed", script, StringComparison.Ordinal);

        var view = Read("_PairTable.cshtml");
        Assert.Contains("a-tag--crossed", view, StringComparison.Ordinal);
        Assert.Contains("CrossedTitle", view, StringComparison.Ordinal);
    }

    // Both rules below used to be sentences in the notebar under the table and are now headed
    // entries in the pair page's reading section — the footer glossary the notebar links to. What
    // is asserted is unchanged and deliberately so: the point was never WHERE the surface says
    // this, it is THAT it says it. A rule the table runs on and does not state is the failure,
    // wherever the missing sentence would have gone.

    /// <summary>
    /// A view's prose with its line breaks flattened, which is what a sentence assertion is
    /// actually about. The source wraps at 100 columns, so "a crossed book is not a narrow one"
    /// carries a newline and eight spaces of indent inside it in one file and not in the next —
    /// and a test that fails when a paragraph is rewrapped fails on the wrong thing. These
    /// assertions have to fail when the sentence is deleted or reworded, and only then.
    /// </summary>
    private static string Prose(string name) =>
        Regex.Replace(Read(name), @"\s+", " ");

    [Fact]
    public void The_reading_section_states_the_crossed_rule_too()
    {
        var reading = Prose("Pair.cshtml");
        Assert.Contains("bid stands at or above its ask", reading, StringComparison.Ordinal);
        Assert.Contains("a crossed book is not a narrow one", reading, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reading_section_states_the_ranking_rule_it_runs_on()
    {
        // The house standard is already set on this surface: phase 2's board limit is announced in
        // the H1 and again above the cards, precisely so a page never shows less than it has
        // without saying so. An unranked figure is the same shape of omission.
        var reading = Prose("Pair.cshtml");
        Assert.Contains("takes no rank", reading, StringComparison.Ordinal);
        Assert.Contains("drops both its marks and its bars", reading, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reading_section_states_the_scope_each_column_is_actually_ranked_in()
    {
        var reading = Prose("Pair.cshtml");

        // THE RULE WAS NOT MOVED, IT WAS REWRITTEN, AND THE REWRITE WAS FALSE. The notebar sentence
        // this replaced carried a qualifier — "for anything priced in the quote currency" — and was
        // printed only on a page that actually held two quotes. The entry that replaced it dropped
        // both, enumerated "the highest bid, the lowest ask, the largest size, turnover, open
        // interest or depth" and then said flatly "They rank only rows quoting in the same asset",
        // which is wrong for three of the columns it names: bid size, ask size and open interest
        // are WholePair. A reader following it read an acid BEST on open interest as "largest USDT
        // book" when it means "largest book on the page" — the exact scope error the entry exists
        // to prevent, committed by the entry.
        Assert.DoesNotContain(
            "They rank only rows quoting in the same asset", reading, StringComparison.Ordinal);

        Assert.Contains(
            "anything priced in the quote currency ranks only against rows quoting in the same asset",
            reading, StringComparison.Ordinal);
        Assert.Contains(
            "the two sizes and open interest are in base units through the venue's own contract"
            + " multiplier, which makes them one measurement, so they rank across every row on the"
            + " page", reading, StringComparison.Ordinal);

        // And the sentence is checked against the specs rather than against itself. These are the
        // two lists the prose above is written from; a column moving between them makes this test
        // fail beside the sentence that would have started lying.
        foreach (var perQuote in new[]
        {
            PairColumn.Bid, PairColumn.Ask, PairColumn.Turnover24h,
            PairColumn.Depth10, PairColumn.Depth25, PairColumn.Depth50
        })
        {
            Assert.Equal(VerdictScope.PerQuoteAsset, Verdicts.Scope(perQuote));
        }

        foreach (var wholePair in new[]
        {
            PairColumn.BidSize, PairColumn.AskSize, PairColumn.OpenInterest, PairColumn.SpreadBps
        })
        {
            Assert.Equal(VerdictScope.WholePair, Verdicts.Scope(wholePair));
        }

        // The spread is the fourth WholePair column and it is ranked under the spread column's own
        // words, so its scope is stated in that entry rather than in this one.
        Assert.Contains(
            "A spread in bps carries no currency, so this column compares every row on the page",
            reading, StringComparison.Ordinal);
    }

    [Fact]
    public void The_notebar_still_carries_the_reader_to_the_rules_it_no_longer_prints()
    {
        // The move is only safe while the link survives. A notebar of two sentences with no way
        // down to the other fourteen rules is not a shorter note, it is a page that stopped saying
        // what it does — so the anchor and its target are asserted as one fact, in the two files
        // that have to keep agreeing about it.
        Assert.Contains("href=\"#reading\"", Read("_PairTable.cshtml"), StringComparison.Ordinal);
        Assert.Contains("id=\"reading\"", Read("Pair.cshtml"), StringComparison.Ordinal);

        // And it has to be a plain fragment: with scripts off this link is the only navigation on
        // the page, so nothing may stand between the press and the jump.
        Assert.DoesNotContain("a-note-link", Read("studio-live.js"), StringComparison.Ordinal);
        Assert.DoesNotContain("a-note-link", Read("studio-ages.js"), StringComparison.Ordinal);
    }
}

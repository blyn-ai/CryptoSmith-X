using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The conversion four venues share and none of them can check for itself.
///
/// A depth band is a NOTIONAL — quote currency inside a band of the mid — so it is computed in the
/// adapter and stored finished, while every OTHER size on the same venue is stored raw and
/// multiplied downstream. That is the same multiplier applied in two different places for two
/// columns of one row, and getting it wrong here is invisible: the number sorts, renders and ranks
/// exactly like a right one, just a hundred or ten thousand times off.
/// </summary>
public sealed class ContractBookTests
{
    /// <summary>Now, not a fixed instant. The tape drops prints stamped outside a window around the
    /// current time, so a hard-coded date would pass on the day it was written and fail two days
    /// later for a reason that has nothing to do with what the test is about.</summary>
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    /// <summary>
    /// A book whose top level on each side sits inside the 25 bps band and whose second level sits
    /// OUTSIDE the 50 bps one.
    ///
    /// The second part is not decoration. <c>DepthMath</c> returns null for a band the book does not
    /// reach past, because a sum bounded by the end of the data is an undercount rather than a
    /// depth — so a fixture whose levels all sit inside the band measures nothing at all. The first
    /// draft of this file had exactly that and the tests caught it.
    ///
    /// Mid is 100.01, so the 25 bps band is 99.76 to 100.26 and takes one level a side: 500 at
    /// 100.00 and 400 at 100.02.
    /// </summary>
    private static (List<(double, double)> Bids, List<(double, double)> Asks) Book() =>
    (
        [(100.0, 500), (99.0, 1_000)],
        [(100.02, 400), (101.0, 900)]
    );

    [Fact]
    public void A_contract_quoted_book_is_worth_its_contract_size_times_the_coin_quoted_one()
    {
        // The whole point: same levels, two venues, one of which counts in hundredths of a coin.
        var (bids, asks) = Book();

        var coins = ContractBook.Compute(bids, asks, contractMultiplier: 1, At);
        var contracts = ContractBook.Compute(bids, asks, contractMultiplier: 0.01, At);

        Assert.NotNull(coins?.Bid25Bps);
        Assert.NotNull(contracts?.Bid25Bps);
        Assert.Equal(coins!.Bid25Bps!.Value * 0.01, contracts!.Bid25Bps!.Value, 6);
        Assert.Equal(coins.Ask25Bps!.Value * 0.01, contracts.Ask25Bps!.Value, 6);
    }

    [Fact]
    public void The_band_is_a_quote_notional_and_not_a_quantity()
    {
        // 500 contracts of 0.01 at a price of 100 is five coins, which is five hundred of quote.
        // A band holding "5" would be a quantity in a column that means money.
        var (bids, asks) = Book();

        var depth = ContractBook.Compute(bids, asks, contractMultiplier: 0.01, At);

        Assert.Equal(500d, depth!.Bid25Bps!.Value, 6);
    }

    [Fact]
    public void A_multiplier_of_one_passes_through_untouched()
    {
        // The coin-quoted case goes through the same call, so a venue that changes its own units
        // needs no different code path here.
        var (bids, asks) = Book();

        var depth = ContractBook.Compute(bids, asks, contractMultiplier: 1, At);

        Assert.Equal(50_000d, depth!.Bid25Bps!.Value, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void An_unusable_contract_size_yields_no_depth_rather_than_a_wrong_one(double multiplier)
    {
        // Discovery may not have reached a symbol yet. Null is "not measured this frame", which the
        // depth columns already read correctly; a band against an assumed size would not be
        // distinguishable from a real one afterwards.
        var (bids, asks) = Book();

        Assert.Null(ContractBook.Compute(bids, asks, multiplier, At));
    }

    [Fact]
    public void An_empty_side_is_no_depth_at_all()
    {
        Assert.Null(ContractBook.Compute([], [(100.0, 1)], 1, At));
        Assert.Null(ContractBook.Compute([(100.0, 1)], [], 1, At));
    }
}

/// <summary>
/// The polled tape shared by the five REST venues. What it must get right is dedup: every one of
/// them re-reads an overlapping page on the next poll, and a tape that forgot would write the same
/// execution again every cycle.
/// </summary>
public sealed class RestTapeTests
{
    /// <summary>Now, not a fixed instant. The tape drops prints stamped outside a window around the
    /// current time, so a hard-coded date would pass on the day it was written and fail two days
    /// later for a reason that has nothing to do with what the test is about.</summary>
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    private static TradeEvent Trade(string id, DateTimeOffset at, double price = 100, double qty = 1) =>
        new("SYM", at, id, null, price, qty, "buy", null);

    [Fact]
    public void A_trade_handed_in_twice_is_handed_on_once()
    {
        var tape = new RestTape();

        tape.Observe("SYM", [Trade("a", At), Trade("b", At)]);
        tape.Observe("SYM", [Trade("a", At), Trade("b", At), Trade("c", At)]);

        var drained = tape.Drain();

        Assert.Equal(3, drained.Count);
        Assert.Equal(["a", "b", "c"], drained.Select(t => t.VenueUid).Order());
    }

    [Fact]
    public void Draining_empties_the_buffer_so_the_collector_never_writes_a_trade_twice()
    {
        var tape = new RestTape();
        tape.Observe("SYM", [Trade("a", At)]);

        Assert.Single(tape.Drain());
        Assert.Empty(tape.Drain());
    }

    [Fact]
    public void Identity_is_per_symbol_because_two_venues_ids_only_have_to_be_unique_within_one()
    {
        var tape = new RestTape();

        tape.Observe("ONE", [Trade("1", At)]);
        tape.Observe("TWO", [Trade("1", At)]);

        Assert.Equal(2, tape.Drain().Count);
    }

    [Fact]
    public void A_delisted_pairs_months_old_print_is_not_handed_on_as_a_recent_trade()
    {
        // A "recent trades" route keeps answering after a pair stops trading, and what it answers
        // with is the last prints it ever had. Those are real trades and they are not recent ones.
        // Stored, they land outside the range the store keeps partitions for, and the failure is not
        // confined to the offending symbol: it fails the whole batch that symbol travelled in, so
        // one delisted pair takes down every live pair's tape with it. Seen on Avantis, then again
        // on Gate.
        var tape = new RestTape();

        tape.Observe("SYM", [Trade("old", At - TimeSpan.FromDays(90)), Trade("now", At)]);

        var drained = tape.Drain();

        Assert.Equal("now", Assert.Single(drained).VenueUid);
    }

    [Fact]
    public void A_print_stamped_in_the_future_is_dropped_from_the_same_guard()
    {
        // The other direction of the same mistake: a field read as the wrong unit, or a venue whose
        // clock runs ahead, lands in a future no partition covers either.
        var tape = new RestTape();

        tape.Observe("SYM", [Trade("ahead", At + TimeSpan.FromDays(1))]);

        Assert.Empty(tape.Drain());
    }

    [Fact]
    public void An_ancient_print_is_remembered_so_a_dead_pairs_page_is_not_re_examined_forever()
    {
        // Dropped is not the same as unseen. A delisted pair's page never changes, so without
        // remembering it the same prints would be walked and rejected on every poll for the life of
        // the process.
        var tape = new RestTape();
        var ancient = Trade("old", At - TimeSpan.FromDays(90));

        tape.Observe("SYM", [ancient]);
        tape.Observe("SYM", [ancient]);

        Assert.Empty(tape.Drain());
    }

    [Fact]
    public void The_last_price_survives_a_drain_because_the_column_that_reads_it_has_no_window()
    {
        var tape = new RestTape();
        tape.Saw("SYM", 101.5, At);
        tape.Observe("SYM", [Trade("a", At, price: 101.5)]);
        tape.Drain();

        Assert.Equal(101.5, tape.Last("SYM")!.Value.Price, 6);
    }

    [Fact]
    public void The_newest_print_by_TIME_is_the_last_one_whatever_order_it_arrived_in()
    {
        // Several of these venues answer newest first and at least one has been seen to interleave.
        var tape = new RestTape();

        tape.Saw("SYM", 100, At);
        tape.Saw("SYM", 102, At.AddSeconds(30));
        tape.Saw("SYM", 101, At.AddSeconds(10));

        Assert.Equal(102d, tape.Last("SYM")!.Value.Price, 6);
    }

    [Fact]
    public void A_symbol_never_polled_has_no_last_price_rather_than_a_zero()
    {
        Assert.Null(new RestTape().Last("SYM"));
    }

    [Fact]
    public void Remembering_is_bounded_so_a_process_that_runs_for_weeks_does_not_grow_without_end()
    {
        // Eviction is FIFO rather than a periodic clear: a clear would let a trade still inside the
        // venue's own page be counted again on the very next poll.
        var tape = new RestTape();
        for (var i = 0; i < 10_000; i++)
        {
            tape.Observe("SYM", [Trade($"id-{i}", At)]);
        }

        Assert.Equal(10_000, tape.Drain().Count);

        // The newest identities are still remembered, which is what the overlap actually needs.
        tape.Observe("SYM", [Trade("id-9999", At)]);
        Assert.Empty(tape.Drain());
    }
}

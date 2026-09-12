using CryptoSmithX.MarketData.Hub.Rollups;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The second cascade: mark and index candles, which until now existed at one minute and nowhere
/// else. Measured on prod before any of this was written — 123,165 index bars for Avantis, all
/// <c>timeframe = 1</c>, and a page whose candle panel opens on the hourly grain, so every one of
/// them was unreachable unless the reader knew to ask for <c>tf=1</c> by hand.
///
/// Why it is a second statement rather than a parameter on the first: the two tables do not have
/// the same columns and cannot honestly share one. <c>market_candle</c> carries volume, trade_count
/// and bar_count; <c>market_price_candle</c> deliberately carries none of them (0032: "объём не
/// заводится: mark/index не торгуются напрямую"). And this table has no <c>updated_at</c> at all,
/// so the touch marker has to be a different column — which is exactly the kind of difference a
/// shared statement would hide until it wrote a wrong row.
///
/// The arithmetic is <see cref="Rollup"/>'s and is tested there; this is the shape of the statement
/// that has to mean the same thing.
/// </summary>
public sealed class PriceRollupTests
{
    [Fact]
    public void Mark_and_index_are_never_aggregated_into_one_bar()
    {
        // The whole hazard of this table in one test. Two series share every other key column, so a
        // GROUP BY that forgets `series` produces a bar whose high came from the mark and whose low
        // came from the index — arithmetically clean, and a number that never existed.
        var sql = RollupJob.PriceSql(boundedBase: true);

        Assert.Contains("c.series = t.series", sql);
        Assert.Contains("group by t.exchange_instrument_id, t.series, t.window_start", sql);
    }

    [Fact]
    public void The_touch_marker_is_a_column_this_table_actually_has()
    {
        // market_candle is driven by updated_at. market_price_candle has no such column (0032), so
        // borrowing the traded statement's WHERE clause would not write a wrong row — it would fail
        // outright, on the host, at the first pass after deploy.
        var sql = RollupJob.PriceSql(boundedBase: true);

        Assert.DoesNotContain("updated_at", sql);
        Assert.Contains("c.received_at >=", sql);
    }

    [Fact]
    public void The_minute_base_is_bounded_on_both_sides_and_a_cascade_step_is_not()
    {
        // Same rule as the traded cascade and for the same reason: the base slice is the unit of
        // progress and must be a bite the pass can finish, while the levels above it consume only
        // what this very pass rewrote moments ago.
        var bounded = RollupJob.PriceSql(boundedBase: true);
        var cascade = RollupJob.PriceSql(boundedBase: false);

        Assert.Contains("@since", bounded);
        Assert.Contains("@until", bounded);
        Assert.DoesNotContain("@since", cascade);
        Assert.Contains("@passStart", cascade);
    }

    [Fact]
    public void Only_a_window_that_has_closed_is_written()
    {
        // A bar for the hour we are standing in would be rewritten every pass and read as final in
        // between. The traded cascade refuses it the same way.
        Assert.Contains("<= now()", RollupJob.PriceSql(boundedBase: true));
    }

    [Fact]
    public void A_bar_of_bars_says_it_was_computed_and_not_fetched()
    {
        // source is how a reader tells a bar the venue sent from one this service made. The column's
        // CHECK named only 'rest' and 'backfill' until 0050, which is why that migration exists.
        Assert.Contains("'derived'", RollupJob.PriceSql(boundedBase: true));
    }

    [Fact]
    public void Open_and_close_are_the_windows_first_and_last_by_time()
    {
        // Not by arrival order. A bar re-fetched after a venue correction arrives last and is not
        // the close unless its own open_time says so.
        var sql = RollupJob.PriceSql(boundedBase: true);

        Assert.Contains("array_agg(c.open  order by c.open_time asc)", sql);
        Assert.Contains("array_agg(c.close order by c.open_time desc)", sql);
    }

    [Fact]
    public void The_price_cascade_climbs_the_same_ladder_as_the_traded_one()
    {
        // One rule for both, so a page cannot offer a timeframe on one series that the other silently
        // lacks. SourceFor is shared rather than reimplemented.
        int[] configured = [5, 15, 60, 240, 720, 1440];

        Assert.Equal(15, RollupJob.SourceFor(60, configured));
        Assert.Equal(720, RollupJob.SourceFor(1440, configured));
    }

    [Fact]
    public void A_rebuilt_bar_replaces_the_one_that_was_there()
    {
        // A late minute repairing its parents is the entire reason this pass re-reads a slack window;
        // an upsert that did nothing on conflict would make that re-read pointless.
        var sql = RollupJob.PriceSql(boundedBase: true);

        Assert.Contains("on conflict (exchange_instrument_id, series, timeframe, open_time) do update set", sql);
        Assert.Contains("close       = excluded.close", sql);
    }
}

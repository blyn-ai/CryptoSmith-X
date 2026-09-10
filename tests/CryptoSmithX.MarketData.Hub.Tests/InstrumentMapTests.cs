using CryptoSmithX.MarketData.Hub.Live;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The id ↔ symbol map the live wire is addressed by. The query behind it is proven the way
/// <see cref="CollectFilterTests"/> proves every collector's — on the SQL constant, without a
/// database — and the lookups are proven on a snapshot built by hand.
/// </summary>
public sealed class InstrumentMapTests
{
    [Fact]
    public void The_map_carries_only_instruments_we_actually_collect()
    {
        // An instrument nobody records has no written age for a live figure to stand beside, so
        // streaming it would be half a row. Same predicate the collectors filter on.
        Assert.Contains("collect = true", InstrumentMap.TargetInstrumentsSql);
        Assert.Contains("status = 'trading'", InstrumentMap.TargetInstrumentsSql);
    }

    [Fact]
    public void Every_column_the_row_needs_is_selected_under_the_name_it_binds_by()
    {
        // This one is here because it shipped broken. The first version selected "instrument_id",
        // which is not a column on this table at all — the key is exchange_instrument.id, and only
        // the tables pointing AT it spell the reference out. It reached the test host before the
        // error did, since a query nothing runs in CI is a query nothing checks.
        //
        // Quoted aliases rather than snake_case, because Dapper is not configured to match names
        // across underscores anywhere in this solution: exchange_symbol would not bind to
        // ExchangeSymbol even where the column does exist.
        Assert.Contains("""id              as "InstrumentId" """.TrimEnd(), InstrumentMap.TargetInstrumentsSql);
        Assert.Contains(""""SegmentCode"""", InstrumentMap.TargetInstrumentsSql);
        Assert.Contains(""""ExchangeSymbol"""", InstrumentMap.TargetInstrumentsSql);
        Assert.DoesNotContain("select instrument_id", InstrumentMap.TargetInstrumentsSql);
    }

    [Fact]
    public void A_symbol_resolves_to_its_id_within_its_own_segment()
    {
        var map = Snapshot();

        Assert.True(map.TryGetId("kraken-futures", "PF_XBTUSD", out var kraken));
        Assert.Equal(1, kraken);
        Assert.True(map.TryGetId("hyperliquid", "BTC", out var hyper));
        Assert.Equal(2, hyper);
    }

    [Fact]
    public void The_same_spelling_on_another_venue_is_another_instrument()
    {
        // The whole reason the wire is addressed by id: BTC on Hyperliquid and BTC on Binance are
        // two listings, and a page comparing them must not conflate the two.
        var map = Snapshot();

        Assert.True(map.TryGetId("hyperliquid", "BTC", out var hyper));
        Assert.False(map.TryGetId("kraken-futures", "BTC", out _));
        Assert.Equal(2, hyper);
    }

    [Fact]
    public void An_unknown_symbol_is_simply_absent()
    {
        // A feed subscribes on its own refresh interval and the map on its own; a symbol one knows
        // and the other does not yet is a minute of skew, not an error.
        Assert.False(Snapshot().TryGetId("kraken-futures", "PF_NEWUSD", out _));
        Assert.False(Snapshot().TryGetSegment(999, out _));
    }

    [Fact]
    public void The_wanted_ids_group_into_one_ask_per_venue_naming_that_venues_symbols()
    {
        // Not "which segments are involved" — which SYMBOLS, per venue. Assembling a quote means
        // summing a book, so a venue asked for its whole listing does that work a thousand times
        // for a page watching one listing; naming the symbols is what keeps the tick affordable.
        var wanted = Snapshot().WantedBySegment([1, 2]);

        Assert.Equal(2, wanted.Count);
        Assert.Equal(["PF_XBTUSD"], wanted.Single(w => w.Segment == "kraken-futures").Symbols);
        Assert.Equal(["BTC"], wanted.Single(w => w.Segment == "hyperliquid").Symbols);
    }

    [Fact]
    public void Two_listings_on_one_venue_are_one_ask_for_both_symbols()
    {
        var map = InstrumentMap.Snapshot.Of(
        [
            new InstrumentMap.Row(1, "binance-usdm", "BTCUSDT"),
            new InstrumentMap.Row(2, "binance-usdm", "BTCUSDC"),
        ]);

        var wanted = Assert.Single(map.WantedBySegment([1, 2]));

        Assert.Equal("binance-usdm", wanted.Segment);
        Assert.Equal(2, wanted.Symbols.Count);
    }

    [Fact]
    public void Ids_that_are_not_on_the_map_ask_no_venue_anything() =>
        Assert.Empty(Snapshot().WantedBySegment([999]));

    private static InstrumentMap.Snapshot Snapshot() =>
        InstrumentMap.Snapshot.Of(
        [
            new InstrumentMap.Row(1, "kraken-futures", "PF_XBTUSD"),
            new InstrumentMap.Row(2, "hyperliquid", "BTC"),
            new InstrumentMap.Row(3, "binance-usdm", "BTCUSDT"),
        ]);
}

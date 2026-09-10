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
    public void The_wanted_ids_name_the_segments_worth_polling()
    {
        // One asset's row set is a handful of venues, and the egress walks those — never every
        // exchange in the process for a page that is not asking about them.
        var segments = Snapshot().SegmentsOf([1, 2]);

        Assert.Equal(2, segments.Count);
        Assert.Contains("kraken-futures", segments);
        Assert.Contains("hyperliquid", segments);
    }

    [Fact]
    public void Ids_that_are_not_on_the_map_name_no_segment_at_all() =>
        Assert.Empty(Snapshot().SegmentsOf([999]));

    private static InstrumentMap.Snapshot Snapshot() =>
        InstrumentMap.Snapshot.Of(
        [
            new InstrumentMap.Row(1, "kraken-futures", "PF_XBTUSD"),
            new InstrumentMap.Row(2, "hyperliquid", "BTC"),
            new InstrumentMap.Row(3, "binance-usdm", "BTCUSDT"),
        ]);
}

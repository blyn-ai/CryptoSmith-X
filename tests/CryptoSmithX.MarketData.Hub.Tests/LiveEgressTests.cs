using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Hub.Live;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The live egress's two promises, proven without a socket or an HTTP connection: what a subscriber
/// is told, and what it is deliberately NOT told.
///
/// The conflation itself belongs to <see cref="Connectors.Streaming.MarketCache{T}"/> — one entry
/// per symbol, last write wins — so what is proven here is the half that lives in this process:
/// polling that cache on a tick collapses a burst into one value, and a value the subscriber
/// already holds does not ride again.
/// </summary>
public sealed class LiveEgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Three_writes_between_ticks_are_one_frame_carrying_the_last_value()
    {
        var clock = new FakeTimeProvider(T0);
        var cache = new Connectors.Streaming.MarketCache<LiveQuote>(clock);
        var conflator = new Conflator();

        cache.Set("PF_XBTUSD", Quote(T0, bid: 1));
        cache.Set("PF_XBTUSD", Quote(T0, bid: 2));
        cache.Set("PF_XBTUSD", Quote(T0, bid: 3));

        var frame = conflator.Changed(cache.FresherThan(TimeSpan.FromSeconds(5)).Select(q => (1, q)));

        Assert.Single(frame);
        Assert.Equal(3, frame[0].Quote.BidPrice);
    }

    [Fact]
    public void An_instrument_whose_value_did_not_change_is_not_in_the_frame()
    {
        var conflator = new Conflator();
        var quote = Quote(T0, bid: 1);

        Assert.Single(conflator.Changed([(1, quote)]));

        // The same cache entry read again on the next tick: nothing was written to it in between,
        // so there is nothing to say. A frame here would be five identical payloads a second.
        Assert.Empty(conflator.Changed([(1, quote)]));
    }

    [Fact]
    public void A_repeat_at_a_new_instant_does_ride_because_the_age_moved()
    {
        var conflator = new Conflator();
        conflator.Changed([(1, Quote(T0, bid: 1))]);

        // Identical figures, fresh observation. Suppressing this would leave the page's live age
        // climbing under a socket that is answering perfectly — staleness that is not there.
        Assert.Single(conflator.Changed([(1, Quote(T0.AddMilliseconds(200), bid: 1))]));
    }

    [Fact]
    public void Only_the_changed_instrument_rides_when_one_of_several_moves()
    {
        var conflator = new Conflator();
        var a = Quote(T0, bid: 1);
        var b = Quote(T0, bid: 2);
        conflator.Changed([(1, a), (2, b)]);

        var frame = conflator.Changed([(1, a), (2, Quote(T0, bid: 9))]);

        Assert.Single(frame);
        Assert.Equal(2, frame[0].InstrumentId);
    }

    [Fact]
    public void A_reset_makes_the_next_tick_a_whole_picture_again()
    {
        var conflator = new Conflator();
        var quote = Quote(T0, bid: 1);
        conflator.Changed([(1, quote)]);
        conflator.Reset();

        // A reconnected subscriber cannot be assumed to still hold what it was told before the gap.
        Assert.Single(conflator.Changed([(1, quote)]));
    }

    [Theory]
    [InlineData("1,2,3", 3)]
    [InlineData(" 1 , 2 ", 2)]
    [InlineData("1,1,1", 1)]
    [InlineData("1,notanumber,2", 2)]   // one unparseable id must not cost the caller every other row
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Ids_are_read_forgivingly(string? query, int expected) =>
        Assert.Equal(expected, LiveEgress.ParseIds(query).Count);

    [Fact]
    public void The_tick_is_two_hundred_milliseconds_and_not_a_setting() =>
        // A budget, not a preference: one connection per studio process, one room per asset and one
        // computation per tick were all sized against this number.
        Assert.Equal(TimeSpan.FromMilliseconds(200), LiveEgress.Tick);

    private static LiveQuote Quote(DateTimeOffset at, double bid) =>
        new("PF_XBTUSD", at, bid, 10, bid + 1, 10, bid, bid, bid, 0.0001, 100, 1_000, null);
}

using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Streaming;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The rule the live tape rests on, stated where the Hub can break it: everything the page's tape
/// shows comes from an <see cref="EventTap{T}"/>, and <see cref="EventBuffer{T}.Drain"/> stays the
/// database's alone.
///
/// A second Drain() would not be a second reader, it would be a thief — the events it returned
/// would be events the recorder never saw, and nothing would say so. That failure is invisible in
/// production (a tape that looks right, a history quietly missing prints), so it is pinned here.
/// </summary>
public sealed class LiveTapeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Watching_a_venues_tape_takes_nothing_from_the_recorder()
    {
        var adapter = new TapeAdapter();
        using var tape = ((IExchangeMarketData)adapter).ObserveTrades(100)!;

        adapter.Publish(Print("1"));
        adapter.Publish(Print("2"));

        Assert.Equal(2, tape.Drain().Count);
        Assert.Equal(2, adapter.DrainTrades().Count);   // the collector's pass is untouched
    }

    [Fact]
    public void A_stalled_viewer_loses_prints_and_the_recorder_loses_none()
    {
        var adapter = new TapeAdapter();
        using var tape = ((IExchangeMarketData)adapter).ObserveTrades(capacity: 2)!;

        for (var i = 0; i < 50; i++)
        {
            adapter.Publish(Print(i.ToString()));
        }

        Assert.Equal(2, tape.Drain().Count);
        Assert.Equal(48, tape.Dropped);
        Assert.Equal(50, adapter.DrainTrades().Count);
    }

    [Fact]
    public void Two_viewers_of_one_venue_each_see_every_print()
    {
        // Per connection, never shared: two connections sharing one tap would take prints from each
        // other exactly the way a second Drain() takes them from the database.
        var adapter = new TapeAdapter();
        using var one = ((IExchangeMarketData)adapter).ObserveTrades(100)!;
        using var two = ((IExchangeMarketData)adapter).ObserveTrades(100)!;

        adapter.Publish(Print("1"));

        Assert.Single(one.Drain());
        Assert.Single(two.Drain());
    }

    [Fact]
    public void A_venue_with_no_socket_offers_no_tape_at_all() =>
        // Null, not an empty tap: "this venue publishes no tape" is the same kind of answer as a
        // null figure in a quote, and the page keeps whatever the collectors recorded.
        Assert.Null(((IExchangeMarketData)new Connectors.Fake.FakeExchangeMarketData()).ObserveTrades(100));

    private static TradeEvent Print(string uid) =>
        new("PF_XBTUSD", T0, uid, null, 77_000, 0.5, "buy", null);

    /// <summary>An adapter shaped like the real ones: one buffer, the collector draining it, the
    /// live path observing it.</summary>
    private sealed class TapeAdapter : IExchangeMarketData
    {
        private readonly EventBuffer<TradeEvent> _trades = new();

        public string SegmentCode => "test-venue";

        public IReadOnlyList<DatasetCapability> Capabilities => [];

        public void Publish(TradeEvent trade) => _trades.Add(trade);

        public IReadOnlyList<TradeEvent> DrainTrades() => _trades.Drain();

        public EventTap<TradeEvent>? ObserveTrades(int capacity) => _trades.Observe(capacity);

        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>([]);

        public Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticker>>([]);

        public Task<IReadOnlyList<Candle>> GetCandles1mAsync(
            string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Candle>>([]);

        public Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
            string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FundingRate>>([]);

        public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct) =>
            Task.FromResult<Depth?>(null);
    }
}

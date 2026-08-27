using CryptoSmithX.MarketData.Connectors.Fake;
using CryptoSmithX.MarketData.Connectors.Market;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

public sealed class FakeExchangeMarketDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 34, 17, TimeSpan.Zero);

    private static FakeExchangeMarketData Adapter(out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Now);
        return new FakeExchangeMarketData(seed: 42, clock);
    }

    [Fact]
    public async Task Candles_are_contiguous_utc_minutes()
    {
        var adapter = Adapter(out _);
        var candles = await adapter.GetCandles1mAsync("FAKE-BTC-USD", Now.AddHours(-2), Now, CancellationToken.None);

        Assert.NotEmpty(candles);
        Assert.All(candles, c => Assert.Equal(0, c.OpenTime.Second));
        Assert.All(candles, c => Assert.Equal(TimeSpan.Zero, c.OpenTime.Offset));

        for (var i = 1; i < candles.Count; i++)
        {
            Assert.Equal(TimeSpan.FromMinutes(1), candles[i].OpenTime - candles[i - 1].OpenTime);
        }
    }

    [Fact]
    public async Task Candles_never_include_the_minute_still_forming()
    {
        var adapter = Adapter(out _);
        var candles = await adapter.GetCandles1mAsync("FAKE-ETH-USD", Now.AddHours(-1), Now.AddHours(1), CancellationToken.None);

        var currentMinute = new DateTimeOffset(Now.Year, Now.Month, Now.Day, Now.Hour, Now.Minute, 0, TimeSpan.Zero);
        Assert.All(candles, c => Assert.True(c.OpenTime < currentMinute, $"{c.OpenTime} is not closed yet"));
    }

    [Fact]
    public async Task Candles_have_a_high_at_or_above_the_low_and_a_body_inside_it()
    {
        var adapter = Adapter(out _);
        var candles = await adapter.GetCandles1mAsync("FAKE-SOL-USD", Now.AddHours(-3), Now, CancellationToken.None);

        Assert.All(candles, c =>
        {
            Assert.True(c.High >= c.Low, $"high {c.High} < low {c.Low}");
            Assert.InRange(c.Open, c.Low, c.High);
            Assert.InRange(c.Close, c.Low, c.High);
            Assert.True(c.Volume > 0);
        });
    }

    [Fact]
    public async Task The_gappy_symbol_is_missing_minutes_so_bar_count_can_be_exercised()
    {
        var adapter = Adapter(out _);
        var from = Now.AddHours(-2);
        var gappy = await adapter.GetCandles1mAsync(FakeExchangeMarketData.GappySymbol, from, Now, CancellationToken.None);
        var solid = await adapter.GetCandles1mAsync("FAKE-BTC-USD", from, Now, CancellationToken.None);

        Assert.True(gappy.Count < solid.Count, "the gappy symbol should be missing minutes");
        Assert.NotEmpty(gappy);
    }

    [Fact]
    public async Task The_same_window_fetched_twice_returns_the_same_bars()
    {
        var adapter = Adapter(out _);
        var first = await adapter.GetCandles1mAsync("FAKE-XRP-USD", Now.AddMinutes(-30), Now, CancellationToken.None);
        var second = await adapter.GetCandles1mAsync("FAKE-XRP-USD", Now.AddMinutes(-30), Now, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Discovery_drops_one_symbol_from_time_to_time()
    {
        var adapter = Adapter(out _);
        var rounds = new List<IReadOnlyList<Instrument>>();
        for (var i = 0; i < 3; i++)
        {
            rounds.Add(await adapter.GetInstrumentsAsync(CancellationToken.None));
        }

        Assert.Contains(rounds, r => r.All(i => i.ExchangeSymbol != FakeExchangeMarketData.FlakySymbol));
        Assert.Contains(rounds, r => r.Any(i => i.ExchangeSymbol == FakeExchangeMarketData.FlakySymbol));
    }

    [Fact]
    public async Task Discovery_normalises_base_assets_and_carries_the_multiplier()
    {
        var adapter = Adapter(out _);
        var instruments = await adapter.GetInstrumentsAsync(CancellationToken.None);

        var pepe = Assert.Single(instruments, i => i.BaseAsset == "PEPE");
        Assert.Equal(1000m, pepe.ContractMultiplier);
        Assert.All(instruments, i => Assert.Equal("USD", i.QuoteAsset));
        Assert.All(instruments, i => Assert.True(i.PriceStep > 0 && i.QtyStep > 0 && i.MinQty > 0));
    }

    [Fact]
    public async Task Tickers_move_between_calls_but_stay_around_the_candle_close()
    {
        var adapter = Adapter(out var clock);
        var first = await adapter.GetTickersAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(20));
        var second = await adapter.GetTickersAsync(CancellationToken.None);

        var a = first.First(t => t.ExchangeSymbol == "FAKE-BTC-USD");
        var b = second.First(t => t.ExchangeSymbol == "FAKE-BTC-USD");

        Assert.NotEqual(a.LastPrice, b.LastPrice);
        Assert.True(b.AskPrice >= b.BidPrice);
        Assert.True(b.OpenInterest > 0);
        Assert.Equal(a.ExchangeSymbol, b.ExchangeSymbol);
    }
}

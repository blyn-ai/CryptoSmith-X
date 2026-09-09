using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// <see cref="CandleCache"/> is the shared logic behind three feeds' "serve a range or refuse the
/// whole thing" rule (WEEX's <c>klineSnapshot</c>/<c>kline</c>, Hyperliquid's <c>candle</c>, and —
/// the parsing bridge aside — Binance's <c>kline</c>), so it is tested here in isolation rather than
/// three times through a socket. The one property every test below is really pinning is the same
/// one: a hole anywhere in the requested window fails the WHOLE call, never a thinned one.
/// </summary>
public sealed class CandleCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static Candle Bar(DateTimeOffset openTime, double close = 100) =>
        new("BTCUSDT", openTime, close, close, close, close, 1, 10);

    [Fact]
    public void An_unseeded_symbol_refuses_any_range()
    {
        var cache = new CandleCache();
        Assert.False(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(1), out var candles));
        Assert.Empty(candles);
    }

    [Fact]
    public void A_bulk_seed_serves_the_range_it_fully_covers()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT",
        [
            Bar(T0, 100),
            Bar(T0.AddMinutes(1), 101),
            Bar(T0.AddMinutes(2), 102),
        ]);

        Assert.True(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(2), out var candles));
        Assert.Equal(2, candles.Count);
        Assert.Equal(100, candles[0].Close);
        Assert.Equal(101, candles[1].Close);
    }

    /// <summary>The whole reason this cache exists: a hole anywhere in the window is indistinguishable
    /// from a venue that traded nothing, so the caller must fall back to REST for the ENTIRE range
    /// rather than receive two bars with a silent gap between them.</summary>
    [Fact]
    public void A_hole_anywhere_in_the_range_fails_the_whole_call_not_just_that_minute()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100), Bar(T0.AddMinutes(2), 102)]);   // T0+1m missing

        Assert.False(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(3), out var candles));
        Assert.Empty(candles);
    }

    [Fact]
    public void Update_upserts_the_currently_forming_bar_in_place()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100)]);
        cache.Update("BTCUSDT", Bar(T0, 105));   // same minute, price moved — still forming

        Assert.True(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(1), out var candles));
        Assert.Equal(105, candles[0].Close);
    }

    [Fact]
    public void Update_appends_a_new_minute_without_disturbing_the_one_before_it()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100)]);
        cache.Update("BTCUSDT", Bar(T0.AddMinutes(1), 101));   // the bar rolled over

        Assert.True(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(2), out var candles));
        Assert.Equal(2, candles.Count);
        Assert.Equal(100, candles[0].Close);
        Assert.Equal(101, candles[1].Close);
    }

    /// <summary>A resubscribe (WEEX) or reconnect must correct a stale buffer, not merge into it —
    /// Seed replaces the whole symbol's history rather than upserting bar by bar.</summary>
    [Fact]
    public void A_second_seed_replaces_the_buffer_rather_than_merging_it()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100), Bar(T0.AddMinutes(1), 101)]);
        cache.Seed("BTCUSDT", [Bar(T0.AddMinutes(5), 999)]);   // fresh, shorter history

        // The old T0 bar is gone: a range spanning it must refuse, not resurrect stale data.
        Assert.False(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(1), out _));
        Assert.True(cache.TryGetRange("BTCUSDT", T0.AddMinutes(5), T0.AddMinutes(6), out var candles));
        Assert.Equal(999, candles[0].Close);
    }

    /// <summary>An empty-but-fully-covered window (no full minute has elapsed yet within it) is a
    /// legitimate answer, not a hole — matches what the REST paths already return for the same
    /// case.</summary>
    [Fact]
    public void A_window_shorter_than_one_minute_is_an_honest_empty_success()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100)]);

        Assert.True(cache.TryGetRange("BTCUSDT", T0.AddSeconds(30), T0.AddSeconds(45), out var candles));
        Assert.Empty(candles);
    }

    [Fact]
    public void Remove_forgets_the_symbol_entirely()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100)]);
        cache.Remove("BTCUSDT");

        Assert.False(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(1), out _));
    }

    /// <summary>Symbols are independent buffers — seeding or updating one must never leak into, or
    /// be blocked by, another.</summary>
    [Fact]
    public void Symbols_do_not_share_a_buffer()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100)]);

        Assert.False(cache.TryGetRange("ETHUSDT", T0, T0.AddMinutes(1), out _));
        Assert.True(cache.TryGetRange("BTCUSDT", T0, T0.AddMinutes(1), out _));
    }

    /// <summary>Mirrors exactly what GetCandles1mAsync's REST paths already filter to: open time
    /// &gt;= from AND open time + 1 minute &lt;= to. A from/to that does not land on a minute
    /// boundary still has to line up against the bars that exist.</summary>
    [Fact]
    public void An_unaligned_from_still_yields_the_bars_whose_open_time_is_within_range()
    {
        var cache = new CandleCache();
        cache.Seed("BTCUSDT", [Bar(T0, 100), Bar(T0.AddMinutes(1), 101)]);

        Assert.True(cache.TryGetRange("BTCUSDT", T0.AddSeconds(30), T0.AddMinutes(2), out var candles));
        Assert.Single(candles);
        Assert.Equal(101, candles[0].Close);
    }
}

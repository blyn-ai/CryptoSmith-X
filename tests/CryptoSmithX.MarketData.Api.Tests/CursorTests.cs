using CryptoSmithX.MarketData.Api;

namespace CryptoSmithX.MarketData.Api.Tests;

/// <summary>
/// The paging token. What matters is that a cursor survives the round trip EXACTLY — a lost
/// microsecond or a mangled symbol makes the next page start in the wrong place, which shows up as
/// duplicated or skipped rows long after the request that caused it.
/// </summary>
public sealed class CursorTests
{
    [Fact]
    public void A_cursor_round_trips_unchanged()
    {
        var original = new Cursor(
            DateTimeOffset.Parse("2026-09-09T12:34:56.7891234Z"), "PF_XBTUSD", "42");

        Assert.True(Cursor.TryDecode(original.Encode(), out var decoded));

        Assert.Equal(original.At, decoded.At);
        Assert.Equal(original.Symbol, decoded.Symbol);
        Assert.Equal(original.Tiebreak, decoded.Tiebreak);
    }

    /// <summary>
    /// Sub-millisecond precision is the point: these tables key on instants that collide at
    /// millisecond resolution constantly, and a cursor rounded to milliseconds would re-serve every
    /// row inside the millisecond it stopped on.
    /// </summary>
    [Fact]
    public void Sub_millisecond_precision_survives()
    {
        var precise = new Cursor(
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567), "S", "");

        Assert.True(Cursor.TryDecode(precise.Encode(), out var decoded));
        Assert.Equal(precise.At.UtcTicks, decoded.At.UtcTicks);
    }

    /// <summary>Venue symbols are venue-chosen. A delimiter that can appear inside one would split
    /// the cursor in the wrong place, so the encoding must survive punctuation.</summary>
    [Theory]
    [InlineData("PF_XBTUSD")]
    [InlineData("cmt_btcusdt")]
    [InlineData("BTC-USD-SWAP")]
    [InlineData("weird,symbol:with|delimiters")]
    public void Symbols_containing_delimiters_survive(string symbol)
    {
        var cursor = new Cursor(DateTimeOffset.UtcNow, symbol, "tie:with,punctuation");

        Assert.True(Cursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal(symbol, decoded.Symbol);
        Assert.Equal("tie:with,punctuation", decoded.Tiebreak);
    }

    [Fact]
    public void An_empty_tiebreak_survives()
    {
        var cursor = new Cursor(DateTimeOffset.Parse("2026-09-09T00:00:00Z"), "S", "");

        Assert.True(Cursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal("", decoded.Tiebreak);
    }

    [Fact]
    public void A_cursor_is_normalised_to_utc()
    {
        var offset = new Cursor(new DateTimeOffset(2026, 9, 9, 14, 0, 0, TimeSpan.FromHours(2)), "S", "");

        Assert.True(Cursor.TryDecode(offset.Encode(), out var decoded));
        Assert.Equal(TimeSpan.Zero, decoded.At.Offset);
        Assert.Equal(12, decoded.At.Hour);
    }

    /// <summary>Anything we did not issue is refused rather than treated as "start from the
    /// beginning": silently restarting hands the caller a page they have already processed and looks
    /// exactly like duplicated data.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")]                       // valid base64, but not three fields
    [InlineData("bm90LWEtZGF0ZR9TH3RpZQ==")]   // three fields, but the first is not an instant
    public void Anything_else_is_refused(string? bogus) =>
        Assert.False(Cursor.TryDecode(bogus, out _));

    /// <summary>The token is opaque by contract. It is base64url so it survives a query string
    /// without escaping, which is the only property a caller may rely on.</summary>
    [Fact]
    public void An_encoded_cursor_is_url_safe()
    {
        var encoded = new Cursor(DateTimeOffset.UtcNow, "PF_XBTUSD", "9").Encode();

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.Equal(Uri.EscapeDataString(encoded), encoded);
    }
}

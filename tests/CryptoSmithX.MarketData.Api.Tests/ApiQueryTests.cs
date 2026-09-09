using CryptoSmithX.MarketData.Api;

namespace CryptoSmithX.MarketData.Api.Tests;

/// <summary>
/// The rules every historical endpoint applies before it touches Postgres. They live in one place so
/// they can be pinned here without a database — the same reason <c>Rollup</c> and <c>Sweep</c> are
/// separable from the loops that call them, and the reason CI needs no Postgres service to run this.
/// </summary>
public sealed class ApiQueryTests
{
    [Fact]
    public void Symbols_are_split_trimmed_and_deduplicated()
    {
        Assert.True(ApiQuery.TryParseSymbols("PF_XBTUSD, PF_ETHUSD ,PF_XBTUSD", null, out var parsed, out _));

        // Order is the caller's, and the repeat is dropped rather than answered twice: a duplicated
        // symbol in the request must not become a duplicated series in the answer.
        Assert.Equal(["PF_XBTUSD", "PF_ETHUSD"], parsed);
    }

    /// <summary>The singular parameter is the one the published contract shipped with; it keeps
    /// working, and the plural wins when both are sent.</summary>
    [Fact]
    public void The_legacy_singular_symbol_is_still_accepted()
    {
        Assert.True(ApiQuery.TryParseSymbols(null, "PF_XBTUSD", out var legacy, out _));
        Assert.Equal(["PF_XBTUSD"], legacy);

        Assert.True(ApiQuery.TryParseSymbols("PF_ETHUSD", "PF_XBTUSD", out var both, out _));
        Assert.Equal(["PF_ETHUSD"], both);
    }

    [Fact]
    public void Asking_for_no_symbols_is_refused_with_the_limit_named()
    {
        Assert.False(ApiQuery.TryParseSymbols(null, null, out _, out var error));
        Assert.Contains("required", error);
        Assert.Contains("50", error);
    }

    [Fact]
    public void More_than_fifty_symbols_is_refused()
    {
        var many = string.Join(',', Enumerable.Range(0, ApiQuery.MaxSymbols + 1).Select(i => $"S{i}"));

        Assert.False(ApiQuery.TryParseSymbols(many, null, out _, out var error));
        Assert.Contains("51", error);
        Assert.Contains("50", error);
    }

    [Fact]
    public void Exactly_fifty_symbols_is_allowed()
    {
        var atTheLimit = string.Join(',', Enumerable.Range(0, ApiQuery.MaxSymbols).Select(i => $"S{i}"));

        Assert.True(ApiQuery.TryParseSymbols(atTheLimit, null, out var parsed, out _));
        Assert.Equal(ApiQuery.MaxSymbols, parsed.Length);
    }

    [Fact]
    public void Both_ends_of_the_window_are_required()
    {
        var t = DateTimeOffset.Parse("2026-09-09T12:00:00Z");

        Assert.False(ApiQuery.TryParseWindow(t, null, out _, out var missingTo));
        Assert.Contains("from and to", missingTo);

        Assert.False(ApiQuery.TryParseWindow(null, t, out _, out var missingFrom));
        Assert.Contains("from and to", missingFrom);
    }

    [Fact]
    public void A_window_that_does_not_move_forward_is_refused()
    {
        var t = DateTimeOffset.Parse("2026-09-09T12:00:00Z");

        Assert.False(ApiQuery.TryParseWindow(t, t, out _, out var equal));
        Assert.Contains("later than", equal);

        Assert.False(ApiQuery.TryParseWindow(t, t.AddHours(-1), out _, out var backwards));
        Assert.Contains("later than", backwards);
    }

    /// <summary>Exactly 48 h is inside the rule; a minute past it is not. The boundary is asserted
    /// from both sides because an off-by-one here silently truncates a caller's window.</summary>
    [Theory]
    [InlineData(47.9, true)]
    [InlineData(48.0, true)]
    [InlineData(48.1, false)]
    [InlineData(72.0, false)]
    public void The_window_is_capped_at_forty_eight_hours(double hours, bool allowed)
    {
        var from = DateTimeOffset.Parse("2026-09-09T00:00:00Z");

        var ok = ApiQuery.TryParseWindow(from, from.AddHours(hours), out _, out var error);

        Assert.Equal(allowed, ok);
        if (!allowed)
        {
            Assert.Contains("48", error);
            Assert.Contains("cursor", error);
        }
    }

    [Fact]
    public void A_parsed_window_is_normalised_to_utc()
    {
        var from = new DateTimeOffset(2026, 9, 9, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.True(ApiQuery.TryParseWindow(from, from.AddHours(1), out var window, out _));

        Assert.Equal(TimeSpan.Zero, window.From.Offset);
        Assert.Equal(12, window.From.Hour);
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(250, 250)]
    [InlineData(999999, 5000)]
    public void Limit_is_clamped_rather_than_refused(int? asked, int expected) =>
        Assert.Equal(expected, ApiQuery.ClampLimit(asked, 1000, 5000));

    /// <summary>No include list means the full answer, so the default response is the useful one
    /// rather than an empty shell a caller has to opt into.</summary>
    [Fact]
    public void An_absent_include_list_includes_everything()
    {
        Assert.True(ApiQuery.Includes(null, "depth"));
        Assert.True(ApiQuery.Includes("", "depth"));
        Assert.True(ApiQuery.Includes("   ", "depth"));
    }

    [Fact]
    public void Include_selects_only_the_named_sections()
    {
        Assert.True(ApiQuery.Includes("instrument,quote", "quote"));
        Assert.True(ApiQuery.Includes("instrument, QUOTE ", "quote"));
        Assert.False(ApiQuery.Includes("instrument,quote", "depth"));
    }
}

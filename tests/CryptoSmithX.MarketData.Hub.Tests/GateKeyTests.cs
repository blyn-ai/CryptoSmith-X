using CryptoSmithX.MarketData.Hub;
using CryptoSmithX.MarketData.Hub.Ingestion;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// Which gate a segment gets, and what ceiling it is built from.
///
/// <b>The unit is the HOST.</b> 0019 keyed the request budget on the venue, because two segments of
/// one exchange share one IP budget — true for perpetuals, and false in both directions the moment
/// spot exists. Binance serves futures and spot from two hosts with two separate ceilings (2 400
/// and 6 000 weight a minute, measured); OKX and Bybit serve both surfaces from one host with one
/// (twenty concurrent tickers split across SWAP and SPOT returned ten successes each). Keyed on the
/// venue, Binance's two budgets collapse into a ceiling neither host has; keyed on the segment, OKX
/// gets two gates where the venue counts one.
///
/// These tests are the four measurements written down as cases, so that a later simplification back
/// to "one budget per exchange" fails here rather than in a venue's refusals.
/// </summary>
public sealed class GateKeyTests
{
    private static ExchangeConfig Segment(string code, string venue, string? baseUrl, int? budget = null) =>
        new()
        {
            Code = code,
            ExchangeCode = venue,
            Adapter = code,
            BaseUrl = baseUrl,
            RequestBudgetPerS = budget,
            Status = "enabled",
        };

    private static VenueConfig Venue(string code, int rps, int concurrency = 8) =>
        new()
        {
            Code = code,
            Name = code,
            RequestBudgetPerS = rps,
            MaxConcurrentRequests = concurrency,
            RequestBudgetSource = "assumed",
        };

    [Fact]
    public void Binances_two_surfaces_are_two_hosts_and_so_two_gates()
    {
        // fapi.binance.com and api.binance.com carry separate ceilings, measured. One gate across
        // both would throttle each to a ceiling neither of them has.
        var futures = Segment("binance-usdm", "binance", "https://fapi.binance.com");
        var spot = Segment("binance-spot", "binance", "https://api.binance.com", budget: 20);
        var all = new[] { futures, spot };
        var venue = Venue("binance", 10);

        var f = ExchangeWorker.ChooseGate(futures, venue, all);
        var s = ExchangeWorker.ChooseGate(spot, venue, all);

        Assert.Equal("fapi.binance.com", f.Host);
        Assert.Equal("api.binance.com", s.Host);
        Assert.NotEqual(f.Host, s.Host);

        // And spot's own number reaches only spot's gate; futures keeps the venue's.
        Assert.Equal(10, f.RequestsPerSecond);
        Assert.Equal(20, s.RequestsPerSecond);
    }

    [Fact]
    public void Okxs_two_surfaces_are_one_host_and_so_one_gate()
    {
        // www.okx.com serves SWAP and SPOT alike. Two gates here would be two ceilings pretending
        // to be one, which is the arrangement the registry exists to prevent.
        var swap = Segment("okx-perp", "okx", "https://www.okx.com");
        var spot = Segment("okx-spot", "okx", "https://www.okx.com");
        var all = new[] { swap, spot };
        var venue = Venue("okx", 5);

        Assert.Equal(
            ExchangeWorker.ChooseGate(swap, venue, all).Host,
            ExchangeWorker.ChooseGate(spot, venue, all).Host);
    }

    [Fact]
    public void Two_segments_on_one_host_that_disagree_get_the_smaller_ceiling()
    {
        // They are handed the SAME gate, so the number it is built from has to be one both can live
        // under. Taking either one silently would apply a ceiling the other never asked for.
        var swap = Segment("okx-perp", "okx", "https://www.okx.com", budget: 5);
        var spot = Segment("okx-spot", "okx", "https://www.okx.com", budget: 12);
        var all = new[] { swap, spot };

        var choice = ExchangeWorker.ChooseGate(spot, Venue("okx", 10), all);

        Assert.Equal(5, choice.RequestsPerSecond);
        Assert.True(choice.Disputed);
        Assert.Equal([("okx-perp", 5), ("okx-spot", 12)], choice.Named);
    }

    [Fact]
    public void Agreeing_on_a_number_is_not_a_dispute()
    {
        // Disputed drives a warning. Two segments that named the same ceiling have nothing to warn
        // about, and a warning that fires anyway is one an operator learns to ignore.
        var swap = Segment("okx-perp", "okx", "https://www.okx.com", budget: 5);
        var spot = Segment("okx-spot", "okx", "https://www.okx.com", budget: 5);

        var choice = ExchangeWorker.ChooseGate(spot, Venue("okx", 10), [swap, spot]);

        Assert.Equal(5, choice.RequestsPerSecond);
        Assert.False(choice.Disputed);
    }

    [Fact]
    public void A_segment_naming_nothing_takes_the_venues_ceiling()
    {
        var only = Segment("gate-perp", "gate", "https://api.gateio.ws");

        var choice = ExchangeWorker.ChooseGate(only, Venue("gate", 10), [only]);

        Assert.Equal("api.gateio.ws", choice.Host);
        Assert.Equal(10, choice.RequestsPerSecond);
        Assert.Empty(choice.Named);
    }

    [Fact]
    public void A_number_named_by_a_segment_on_another_host_does_not_reach_this_one()
    {
        // The whole point of the key. Spot's twenty is about api.binance.com and says nothing about
        // fapi.binance.com — reading it as if it did is exactly the mistake the venue key made.
        var futures = Segment("binance-usdm", "binance", "https://fapi.binance.com");
        var spot = Segment("binance-spot", "binance", "https://api.binance.com", budget: 20);

        var choice = ExchangeWorker.ChooseGate(futures, Venue("binance", 10), [futures, spot]);

        Assert.Equal(10, choice.RequestsPerSecond);
        Assert.Empty(choice.Named);
    }

    [Theory]
    [InlineData("https://api.bybit.com", "api.bybit.com")]
    [InlineData("https://www.okx.com/", "www.okx.com")]
    [InlineData("https://API.Binance.com", "api.binance.com")]
    public void The_host_is_the_hostname_and_nothing_around_it(string baseUrl, string expected)
    {
        // A path, a trailing slash or a capital must not split one host into two gates.
        Assert.Equal(expected, ExchangeWorker.HostOf(Segment("x", "v", baseUrl)));
    }

    [Fact]
    public void A_segment_with_no_base_url_shares_a_gate_with_nothing()
    {
        // The fake, and any segment seeded into the catalogue before its adapter exists. Falling
        // back to its own code is the truth about a segment that has no host to share.
        var seeded = Segment("bybit-spot", "bybit", baseUrl: null);
        var live = Segment("bybit-perp", "bybit", "https://api.bybit.com");

        Assert.Equal("bybit-spot", ExchangeWorker.HostOf(seeded));
        Assert.NotEqual(ExchangeWorker.HostOf(live), ExchangeWorker.HostOf(seeded));
    }
}

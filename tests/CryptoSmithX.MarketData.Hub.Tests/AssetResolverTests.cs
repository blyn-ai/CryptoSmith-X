using CryptoSmithX.MarketData.Hub.Ingestion;

namespace CryptoSmithX.MarketData.Hub.Tests;

public sealed class AssetResolverTests
{
    private static Dictionary<string, AliasHit> Aliases(params (string Alias, string Canon, decimal Mult)[] rows) =>
        rows.ToDictionary(r => r.Alias, r => new AliasHit(r.Canon, r.Mult), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void An_unknown_raw_is_its_own_canon_with_multiplier_one()
    {
        var hit = AssetResolver.Resolve("SOL", Aliases(), Aliases());
        Assert.Equal("SOL", hit.Canon);
        Assert.Equal(1m, hit.Multiplier);
    }

    [Fact]
    public void A_global_alias_maps_the_raw_to_its_canon()
    {
        var hit = AssetResolver.Resolve("XBT", Aliases(), Aliases(("XBT", "BTC", 1m)));
        Assert.Equal("BTC", hit.Canon);
        Assert.Equal(1m, hit.Multiplier);
    }

    [Fact]
    public void An_exchange_alias_wins_over_a_global_one()
    {
        // The same raw resolves differently per venue: the exchange-specific row takes precedence.
        var exchange = Aliases(("MPEPE", "PEPE", 1_000_000m));
        var global = Aliases(("MPEPE", "PEPE", 1m));

        var hit = AssetResolver.Resolve("MPEPE", exchange, global);

        Assert.Equal("PEPE", hit.Canon);
        Assert.Equal(1_000_000m, hit.Multiplier);
    }

    [Fact]
    public void The_alias_multiplier_multiplies_the_instruments_own()
    {
        // 1000PEPE -> PEPE carries a 1000 multiplier; the instrument's own is 1, so the effective
        // contract multiplier the store writes is the product. This is what DiscoveryCollector does.
        var hit = AssetResolver.Resolve("1000PEPE", Aliases(), Aliases(("1000PEPE", "PEPE", 1000m)));
        const decimal instrumentMultiplier = 1m;

        Assert.Equal("PEPE", hit.Canon);
        Assert.Equal(1000m, instrumentMultiplier * hit.Multiplier);
    }

    [Fact]
    public void Resolution_is_case_insensitive_on_the_raw()
    {
        var hit = AssetResolver.Resolve("xbt", Aliases(), Aliases(("XBT", "BTC", 1m)));
        Assert.Equal("BTC", hit.Canon);
    }
}

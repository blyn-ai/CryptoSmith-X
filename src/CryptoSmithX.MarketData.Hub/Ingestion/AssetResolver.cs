namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>What a raw venue asset resolved to: the canonical asset and the alias's multiplier.</summary>
public readonly record struct AliasHit(string Canon, decimal Multiplier);

/// <summary>
/// Turns a venue's raw asset string into a canonical asset. The rule, in order: a alias defined for
/// this exchange wins, then a global alias, then identity (an unknown raw is its own canon with
/// multiplier 1). Pure and dictionary-driven so it is unit-tested without a database — the
/// <see cref="DiscoveryCollector"/> does the one batch query that fills the dictionaries.
/// </summary>
public static class AssetResolver
{
    public static AliasHit Resolve(
        string raw,
        IReadOnlyDictionary<string, AliasHit> exchangeAliases,
        IReadOnlyDictionary<string, AliasHit> globalAliases)
    {
        if (exchangeAliases.TryGetValue(raw, out var forExchange))
        {
            return forExchange;
        }

        if (globalAliases.TryGetValue(raw, out var global))
        {
            return global;
        }

        // Identity: the raw is already canonical. Multiplier 1 leaves the instrument's own untouched.
        return new AliasHit(raw, 1m);
    }
}

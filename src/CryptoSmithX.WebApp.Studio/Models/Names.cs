namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// Internal identifiers, said the way a reader says them.
///
/// Band 5 printed <c>binance-usdm · candles_index</c> and <c>hyperliquid · depth</c> — the segment
/// code and the dataset code, exactly as they sit in the database. They are ours: the venue is
/// "Binance" and the surface is its USD-M futures, the dataset is "index candles". A shopfront that
/// prints its own primary keys is asking the reader to learn our schema before they can read a
/// sentence about a gap.
///
/// The mapping is deliberately dumb and total: an unknown code comes back tidied rather than
/// translated, because inventing a name for something we have not thought about is worse than
/// showing the code we do have.
/// </summary>
public static class Names
{
    public static string Segment(string code) => code switch
    {
        "binance-usdm" => "Binance USD-M",
        "binance-coinm" => "Binance COIN-M",
        "binance-spot" => "Binance spot",
        "weex-futures" => "WEEX futures",
        "kraken-futures" => "Kraken futures",
        "kraken-spot" => "Kraken spot",
        "hyperliquid" => "Hyperliquid",
        "okx-swap" => "OKX swap",
        "coinbase-spot" => "Coinbase spot",
        _ => Tidy(code),
    };

    public static string Dataset(string code) => code switch
    {
        "snapshot" => "Snapshot",
        "depth" => "Depth bands",
        "book" => "Level book",
        "candles" => "Candles 1m",
        "candles_mark" => "Mark candles",
        "candles_index" => "Index candles",
        "funding" => "Funding",
        "open_interest" => "Open interest",
        "trades" => "Trades",
        "liquidations" => "Liquidations",
        "discovery" => "Discovery",
        "rollup" => "Rollup",
        "spec_versions" => "Spec versions",
        "vault_pair_state" => "Vault pair state",
        "vault_state" => "Vault state",
        "reference_depth" => "Reference depth",
        _ => Tidy(code),
    };

    /// <summary>Hyphens and underscores out, first letter up. What a code looks like when nobody has
    /// decided how to say it — readable, and still obviously the code.</summary>
    private static string Tidy(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code;
        }

        var text = code.Replace('_', ' ').Replace('-', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

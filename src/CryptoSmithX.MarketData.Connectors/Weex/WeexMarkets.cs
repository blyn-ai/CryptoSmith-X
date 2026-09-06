using System.Globalization;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// The two facts about WEEX identity that both the REST adapter and the WS feed have to agree on,
/// kept in one place so they cannot drift apart. They used to be private helpers on
/// <see cref="WeexFuturesMarketData"/>; the WS feed needs exactly the same rules — which symbols are
/// real markets, and how a v2 symbol spells itself in v3 — and a second copy of either would be a
/// second definition of "the same instrument".
/// </summary>
internal static class WeexMarkets
{
    /// <summary>'cmt_btcusdt' → 'BTCUSDT': the transform v3's Binance-format symbols use. Verified
    /// against a live snapshot: 1011 of 1023 v2 symbols match a v3 book-ticker entry this way; the
    /// rest are dead or exotic listings the adapter already treats as absent (see the recon note).
    ///
    /// Deliberately one-way. The WS socket speaks v3 while every stored identity is v2, and the
    /// inverse spelling ('BTCUSDT' → 'cmt_btcusdt') is NOT derivable — this transform has a branch
    /// for symbols that carry no 'cmt_' prefix, so more than one v2 symbol can map onto one v3
    /// symbol. The feed therefore carries a map built from the venue's own symbol list rather than
    /// inventing a reverse rule.</summary>
    public static string ToV3Symbol(string v2Symbol) =>
        v2Symbol.StartsWith("cmt_", StringComparison.Ordinal)
            ? v2Symbol[4..].ToUpperInvariant()
            : v2Symbol.ToUpperInvariant();

    /// <summary>A contract with real trades — not just a non-zero reference price. Found live:
    /// cmt_usdcusdt carries last=1.0006 with volume_24h=0, and its /candles call still 400s.</summary>
    public static bool IsLive(WeexTicker t) => Parse(t.Last) > 0 && Parse(t.Volume24h) > 0;

    public static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}

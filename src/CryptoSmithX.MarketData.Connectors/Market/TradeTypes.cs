namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// The only <c>trade.trade_type</c> values the schema accepts (0032), and the one place a venue's
/// own word for it is checked against them.
///
/// Anything a venue calls something else becomes NULL, not 'fill'. That is the column's own rule —
/// "NULL = unknown, NOT the same as 'fill'; do not default" — and it is also what keeps a new venue
/// word (Kraken has published <c>assignment</c>) from either failing the CHECK on insert or being
/// quietly recorded as an ordinary trade.
/// </summary>
public static class TradeTypes
{
    public const string Fill = "fill";
    public const string Liquidation = "liquidation";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        Fill, Liquidation, "partial_liquidation", "termination", "block",
    };

    public static string? Normalize(string? venueWord) =>
        venueWord is not null && Allowed.Contains(venueWord) ? venueWord : null;
}

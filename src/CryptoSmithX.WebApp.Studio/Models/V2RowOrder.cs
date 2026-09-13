namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// The order band 1 lists its rows in, and where one quote's block ends.
///
/// By the quote first — USD, USDT, USDC, then any other quote by its code — because every price,
/// turnover and depth on the row is denominated in it and ranks only inside it; a block per quote is
/// what lets a reader see which rows a MAX or MIN was taken across. Then perpetuals before spot, then
/// the venue by name, so a venue is found where the alphabet puts it rather than where its turnover
/// happened to land this minute.
///
/// Fixed, and not re-sorted by the picked measurement: a table whose rows move when a band below it
/// changes breaks the blocks the quote draws.
/// </summary>
public static class V2RowOrder
{
    private static readonly string[] LeadingQuotes = ["USD", "USDT", "USDC"];

    public static IReadOnlyList<VenueRowModel> Sort(IEnumerable<VenueRowModel> rows) =>
        rows.OrderBy(r => QuoteRank(r.Row.QuoteAsset))
            .ThenBy(r => r.Row.QuoteAsset, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => KindRank(r.Row.SegmentKind))
            .ThenBy(r => r.Row.ExchangeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whether <paramref name="row"/> opens a new quote block after <paramref name="previous"/>.</summary>
    public static bool StartsBlock(VenueRowModel? previous, VenueRowModel row) =>
        previous is not null && !string.Equals(previous.Row.QuoteAsset, row.Row.QuoteAsset, StringComparison.OrdinalIgnoreCase);

    private static int QuoteRank(string quote)
    {
        var i = Array.FindIndex(LeadingQuotes, q => string.Equals(q, quote, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : LeadingQuotes.Length;
    }

    private static int KindRank(string kind) => kind switch
    {
        "perp" => 0,
        "spot" => 1,
        _ => 2,
    };
}

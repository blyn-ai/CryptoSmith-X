using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// Band 1's order, asked for by the owner 2026-09-13: by quote — USD, USDT, USDC, then the rest — then
/// perpetual before spot, then the venue by name; a rule where one quote's block ends.
/// </summary>
public sealed class V2RowOrderTests
{
    [Fact]
    public void Quote_then_perp_before_spot_then_venue_by_name()
    {
        var rows = new[]
        {
            Row(1, "WEEX", "USDT", "perp"),
            Row(2, "Binance", "USDC", "perp"),
            Row(3, "Nado", "USDT0", "perp"),
            Row(4, "Kraken", "USD", "perp"),
            Row(5, "Binance", "USDT", "spot"),
            Row(6, "avantis", "USD", "perp"),
            Row(7, "Bybit", "USDT", "perp"),
            Row(8, "Binance", "USDT", "perp"),
        };

        var sorted = V2RowOrder.Sort(rows).Select(r => r.Row.InstrumentId);

        Assert.Equal([6, 4, 8, 7, 1, 5, 2, 3], sorted);
    }

    [Fact]
    public void A_block_starts_where_the_quote_changes_and_not_at_the_top()
    {
        var sorted = V2RowOrder.Sort([Row(1, "Kraken", "USD", "perp"), Row(2, "Binance", "USDT", "perp"), Row(3, "Bybit", "USDT", "perp")]);

        Assert.False(V2RowOrder.StartsBlock(null, sorted[0]));
        Assert.True(V2RowOrder.StartsBlock(sorted[0], sorted[1]));
        Assert.False(V2RowOrder.StartsBlock(sorted[1], sorted[2]));
    }

    private static VenueRowModel Row(int id, string venue, string quote, string kind) =>
        Rows.At(Rows.Venue(id, quote: quote) with { ExchangeName = venue, SegmentKind = kind });
}

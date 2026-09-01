using System.Globalization;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>
/// One <c>l2Book</c> response serves both the ticker's top-of-book and the depth collector's
/// cumulative notional — shared by <see cref="HyperliquidBookFeed"/> (REST) and
/// <see cref="HyperliquidWsFeed"/> (WS) so the two never drift.
/// </summary>
public static class HyperliquidBookMath
{
    /// <summary><see langword="null"/> for an empty side — a delisted or otherwise market-less coin
    /// serves <c>levels: [[],[]]</c>, not an error.</summary>
    public static (BookTop? Top, Depth? Depth) Compute(HlL2Book book, DateTimeOffset at)
    {
        if (book.Levels.Count != 2)
        {
            return (null, null);
        }

        var bids = book.Levels[0].ConvertAll(l => (Parse(l.Px), Parse(l.Sz)));
        var asks = book.Levels[1].ConvertAll(l => (Parse(l.Px), Parse(l.Sz)));
        if (bids.Count == 0 || asks.Count == 0)
        {
            return (null, null);
        }

        // Levels arrive best-first per the venue (confirmed live); the top entry is the top of book.
        var top = new BookTop(BidPrice: bids[0].Item1, BidSize: bids[0].Item2, AskPrice: asks[0].Item1, AskSize: asks[0].Item2);
        var depth = DepthMath.Compute(bids, asks, at);
        return (top, depth);
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}

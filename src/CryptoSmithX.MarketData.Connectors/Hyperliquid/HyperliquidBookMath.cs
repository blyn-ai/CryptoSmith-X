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

    /// <summary>
    /// The same response kept whole for <c>book_topn</c> (0032) rather than reduced to bands. The
    /// levels arrive best-first from the venue and are re-sorted anyway by
    /// <see cref="BookFrames.From"/> — one ordering rule for all four venues.
    /// </summary>
    /// <param name="seq">Hyperliquid publishes no sequence number, so the frame's own millisecond
    /// stands in: monotonic per coin on this feed, which is what the primary key needs.</param>
    public static BookFrame? ToFrame(string coin, HlL2Book book, DateTimeOffset at, long seq)
    {
        if (book.Levels.Count != 2)
        {
            return null;
        }

        var bids = new List<KeyValuePair<double, double>>(book.Levels[0].Count);
        foreach (var l in book.Levels[0]) { bids.Add(new(Parse(l.Px), Parse(l.Sz))); }
        var asks = new List<KeyValuePair<double, double>>(book.Levels[1].Count);
        foreach (var l in book.Levels[1]) { asks.Add(new(Parse(l.Px), Parse(l.Sz))); }

        // Whatever the venue sent IS the depth here — l2Book has no subscribed level count to
        // compare against, unlike WEEX's @depth200.
        var levels = Math.Max(bids.Count, asks.Count);
        return levels == 0 ? null : BookFrames.From(coin, bids, asks, at, seq, levels);
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}

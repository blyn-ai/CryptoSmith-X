namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// Turning a maintained book's price→qty maps into the top-N frame <c>book_topn</c> stores. One
/// implementation for all three venues that keep a raw book (WEEX, Binance, Kraken), for the same
/// reason <see cref="Kraken.DepthMath"/> is shared: the ordering rule — best price first, per side —
/// is the definition of "top of book", and three copies of it would be three chances to disagree.
/// </summary>
public static class BookFrames
{
    /// <summary>
    /// Null when either side is empty: a frame with no bids or no asks is not a book, and
    /// <c>book_topn</c>'s cardinality CHECK would take it while the row would say nothing.
    /// Levels shorter than <paramref name="levels"/> are kept as they are — the schema's own
    /// comment says the arrays may be shorter than the subscribed depth on a thin side.
    /// </summary>
    public static BookFrame? From(
        string exchangeSymbol,
        IReadOnlyCollection<KeyValuePair<double, double>> bids,
        IReadOnlyCollection<KeyValuePair<double, double>> asks,
        DateTimeOffset observedAt,
        long seq,
        int levels)
    {
        var bid = Top(bids, levels, descending: true);
        var ask = Top(asks, levels, descending: false);
        if (bid.Count == 0 || ask.Count == 0)
        {
            return null;
        }

        return new BookFrame(
            exchangeSymbol, observedAt, seq, IsSnapshot: true, levels,
            BidPrices: bid.ConvertAll(l => l.Price),
            BidQuantities: bid.ConvertAll(l => l.Qty),
            AskPrices: ask.ConvertAll(l => l.Price),
            AskQuantities: ask.ConvertAll(l => l.Qty));
    }

    private static List<(double Price, double Qty)> Top(
        IReadOnlyCollection<KeyValuePair<double, double>> side, int levels, bool descending)
    {
        var live = new List<(double Price, double Qty)>(side.Count);
        foreach (var (price, qty) in side)
        {
            // A zero level is a removal the venue has not garbage-collected, not a resting order.
            if (qty > 0 && price > 0)
            {
                live.Add((price, qty));
            }
        }

        live.Sort((a, b) => descending ? b.Price.CompareTo(a.Price) : a.Price.CompareTo(b.Price));
        if (live.Count > levels)
        {
            live.RemoveRange(levels, live.Count - levels);
        }

        return live;
    }
}

using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

/// <summary>
/// Cumulative-notional depth from an order book, one implementation shared by the REST path (levels
/// straight off /orderbook) and the WS path (levels off the maintained book) so the two never drift.
/// Semantics follow the 0001 DDL: quote notional within a band of the mid, and null when the book
/// does not reach past the band — an unbounded sum would be an undercount and the column must stay
/// empty rather than lie.
/// </summary>
public static class DepthMath
{
    public static Depth? Compute(
        IReadOnlyList<(double Price, double Qty)> bids,
        IReadOnlyList<(double Price, double Qty)> asks,
        DateTimeOffset at)
    {
        if (bids.Count == 0 || asks.Count == 0)
        {
            return null;
        }

        var bestBid = double.MinValue;
        foreach (var (price, qty) in bids)
        {
            if (qty > 0 && price > bestBid)
            {
                bestBid = price;
            }
        }

        var bestAsk = double.MaxValue;
        foreach (var (price, qty) in asks)
        {
            if (qty > 0 && price < bestAsk)
            {
                bestAsk = price;
            }
        }

        if (bestBid == double.MinValue || bestAsk == double.MaxValue)
        {
            return null;
        }

        var mid = (bestBid + bestAsk) / 2;
        return new Depth(
            Bid10Bps: BandBid(bids, mid, 10),
            Ask10Bps: BandAsk(asks, mid, 10),
            Bid25Bps: BandBid(bids, mid, 25),
            Ask25Bps: BandAsk(asks, mid, 25),
            Bid50Bps: BandBid(bids, mid, 50),
            Ask50Bps: BandAsk(asks, mid, 50),
            At: at);
    }

    private static double? BandBid(IReadOnlyList<(double Price, double Qty)> levels, double mid, int bps)
    {
        var floor = mid * (1 - (bps / 10_000.0));
        var sum = 0.0;
        var bounded = false;
        foreach (var (price, qty) in levels)
        {
            if (qty <= 0)
            {
                continue;
            }

            if (price >= floor)
            {
                sum += price * qty;
            }
            else
            {
                bounded = true;
            }
        }

        return bounded ? sum : null;
    }

    private static double? BandAsk(IReadOnlyList<(double Price, double Qty)> levels, double mid, int bps)
    {
        var ceiling = mid * (1 + (bps / 10_000.0));
        var sum = 0.0;
        var bounded = false;
        foreach (var (price, qty) in levels)
        {
            if (qty <= 0)
            {
                continue;
            }

            if (price <= ceiling)
            {
                sum += price * qty;
            }
            else
            {
                bounded = true;
            }
        }

        return bounded ? sum : null;
    }
}

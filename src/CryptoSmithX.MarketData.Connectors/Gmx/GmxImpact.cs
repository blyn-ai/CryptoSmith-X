namespace CryptoSmithX.MarketData.Connectors.Gmx;

/// <summary>
/// GMX's position price impact, ported from the venue's own SDK — gmx-interface
/// <c>sdk/src/utils/fees/priceImpact.ts</c> (getPriceImpactForPosition, getPriceImpactUsd,
/// getCappedPositionImpactUsd) and <c>sdk/src/utils/prices/utils.ts</c> (getAcceptablePriceInfo,
/// getAcceptablePriceByPriceImpact), read 2026-09-13.
///
/// <b>What it answers.</b> The price the venue's own interface quotes for a MARKET INCREASE of a stated
/// USD size: opening long at the oracle's max price, opening short at its min, each moved by the impact of
/// what the order does to the long/short imbalance. That is the executable quote at a size, the same kind
/// of figure Avantis's risk engine returns — computed here from published parameters instead of asked for,
/// because the venue publishes the function and every input to it.
///
/// <b>Floating point on purpose.</b> The SDK itself evaluates the power in JavaScript doubles
/// (applyImpactFactor), so doubles reproduce the venue's arithmetic rather than approximate it.
///
/// Only increases are modelled: a quote is the price to open. Decreases apply the same impact with a cap
/// and a pending-impact settlement that belongs to a position, not to a quote.
/// </summary>
internal sealed record GmxImpact(
    double MinPrice,
    double MaxPrice,
    double LongOiUsd,
    double ShortOiUsd,
    bool TokensForBalance,
    double FactorPositive,
    double FactorNegative,
    double ExponentPositive,
    double ExponentNegative,
    double MaxFactorPositive,
    double MaxFactorNegative,
    bool HasVirtualInventory,
    double VirtualInventoryUsd)
{
    public double Mid => (MinPrice + MaxPrice) / 2;

    /// <summary>Impact in USD of opening <paramref name="sizeUsd"/> on one side — positive helps the
    /// trader. The SDK's getCappedPositionImpactUsd with shouldCapNegativeImpact false, which is what its
    /// acceptable price uses: a positive impact is capped, a negative one is not.</summary>
    public double ImpactUsd(double sizeUsd, bool isLong)
    {
        var delta = sizeUsd;
        if (TokensForBalance)
        {
            // convertToTokenAmountForIncrease at the side's own price, back to USD at mid.
            var price = isLong ? MaxPrice : MinPrice;
            delta = price > 0 ? sizeUsd / price * Mid : 0;
        }

        var (nextLong, nextShort) = isLong ? (LongOiUsd + delta, ShortOiUsd) : (LongOiUsd, ShortOiUsd + delta);
        var impact = PriceImpactUsd(LongOiUsd, ShortOiUsd, nextLong, nextShort);

        if (impact < 0 && HasVirtualInventory)
        {
            var (vLong, vShort) = VirtualInventoryUsd > 0 ? (0d, VirtualInventoryUsd) : (-VirtualInventoryUsd, 0d);
            var (vNextLong, vNextShort) = isLong ? (vLong + delta, vShort) : (vLong, vShort + delta);
            var virtualImpact = PriceImpactUsd(vLong, vShort, vNextLong, vNextShort);
            if (virtualImpact < impact)
            {
                impact = virtualImpact;
            }
        }

        if (impact > 0)
        {
            // getMaxPositionImpactFactors: the positive cap never exceeds the negative one.
            var cap = Math.Min(MaxFactorPositive, MaxFactorNegative) * Math.Abs(sizeUsd);
            impact = Math.Min(impact, cap);
        }

        return impact;
    }

    /// <summary>getAcceptablePriceByPriceImpact for an increase: a long pays max price, less impact per
    /// unit of size; a short receives min price, plus it.</summary>
    public double Ask(double sizeUsd) => MaxPrice * (sizeUsd - ImpactUsd(sizeUsd, isLong: true)) / sizeUsd;

    public double Bid(double sizeUsd) => MinPrice * (sizeUsd + ImpactUsd(sizeUsd, isLong: false)) / sizeUsd;

    /// <summary>The cost of one side against the oracle mid, in bps — positive is worse than mid.</summary>
    public double CostBps(double sizeUsd, bool isLong)
    {
        var mid = Mid;
        return isLong ? (Ask(sizeUsd) - mid) / mid * 10_000 : (mid - Bid(sizeUsd)) / mid * 10_000;
    }

    /// <summary>
    /// The largest size, up to <paramref name="capacityUsd"/>, whose quote stays within
    /// <paramref name="bps"/> of mid. Zero when even a dollar costs more — the oracle's own min/max spread
    /// can do that on its own.
    ///
    /// Bisection, because the cost is not monotone near zero: an order that improves the imbalance is paid
    /// for it (a positive impact) until it crosses over, and only then does size cost. Past the crossover
    /// the power law only grows, which is what makes the boundary unique.
    /// </summary>
    public double SizeWithin(double bps, bool isLong, double capacityUsd)
    {
        if (!(capacityUsd > 0) || Mid <= 0)
        {
            return 0;
        }

        if (CostBps(capacityUsd, isLong) <= bps)
        {
            return capacityUsd;
        }

        double lo = 0, hi = capacityUsd;
        if (CostBps(Math.Min(1, capacityUsd), isLong) > bps)
        {
            return 0;
        }

        for (var i = 0; i < 80 && hi - lo > 0.01; i++)
        {
            var probe = (lo + hi) / 2;
            if (CostBps(probe, isLong) <= bps)
            {
                lo = probe;
            }
            else
            {
                hi = probe;
            }
        }

        return lo;
    }

    /// <summary>getPriceImpactUsd: same-side rebalance or crossover, per PricingUtils.sol.</summary>
    private double PriceImpactUsd(double currentLong, double currentShort, double nextLong, double nextShort)
    {
        var currentDiff = Math.Abs(currentLong - currentShort);
        var nextDiff = Math.Abs(nextLong - nextShort);
        var sameSide = currentLong <= currentShort == nextLong <= nextShort;

        if (sameSide)
        {
            var positive = nextDiff < currentDiff;
            var factor = positive ? FactorPositive : FactorNegative;
            var exponent = positive ? ExponentPositive : ExponentNegative;
            var delta = Math.Abs(Apply(currentDiff, factor, exponent) - Apply(nextDiff, factor, exponent));
            return positive ? delta : -delta;
        }

        var positiveImpact = Apply(currentDiff, FactorPositive, ExponentPositive);
        var negativeImpact = Apply(nextDiff, FactorNegative, ExponentNegative);
        var crossover = Math.Abs(positiveImpact - negativeImpact);
        return positiveImpact > negativeImpact ? crossover : -crossover;
    }

    /// <summary>applyImpactFactor: diff^exponent × factor, a non-finite power reading as zero as in the SDK.</summary>
    private static double Apply(double diff, double factor, double exponent)
    {
        var powered = Math.Pow(diff, exponent);
        return double.IsFinite(powered) ? powered * factor : 0;
    }
}

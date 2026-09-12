namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// How much more this venue will take on each side right now — the figure the Bid/Ask size columns
/// hold.
///
/// <b>A port, not an invention.</b> This is the venue's own <c>availableLiquidity</c>, which its SDK
/// describes as mirroring <c>avantis-ui-v2 lib/trade.ts</c>: the same function its own trading
/// screen uses to decide what it will let a user open. Every input is a ceiling the venue publishes
/// in the catalogue we already fetch, so this costs no request at all — which is why it is here and
/// not on the quote endpoint's slower sweep.
///
/// <b>Why it belongs in the size columns.</b> A book venue's bid size is "how much is resting at the
/// best bid" — the amount the venue will transact on that side before the price moves off the top.
/// Here nothing rests, and the binding figure is instead the smallest of the venue's own caps. Both
/// answer "how much will this venue take on this side right now", which is what the column is for.
/// The distinction between resting and quoted lives in the badge, the same as for depth.
///
/// <b>Which side is which.</b> <c>liquidity.buy</c> bounds the LONG side and <c>liquidity.sell</c>
/// the SHORT — taken from the SDK's own parameter mapping rather than guessed from the words. Long
/// is a buyer, so it is the ask side; short is a seller, so it is the bid.
/// </summary>
public static class AvantisCapacity
{
    /// <summary>
    /// Maximum additional notional in the quote asset, per side, or null where the venue's own
    /// inputs are missing.
    ///
    /// <paramref name="walletOi"/> is zero here and not a parameter: the venue's function subtracts
    /// a specific trader's existing exposure from their personal cap, and this figure is about the
    /// market rather than about anybody in it. Stated rather than silently dropped, because a reader
    /// comparing this to what their own screen shows should know exactly which term differs.
    /// </summary>
    public static (double? Long, double? Short) Available(
        double? maxOpenInterest,
        double? totalOi,
        double? groupMaxOi,
        double? groupOi,
        double? maxWalletOi,
        double? groupOpenInterestPercentageP,
        double? maxLongOiP,
        double? maxShortOiP,
        double? pairMaxOi,
        double? longOi,
        double? shortOi,
        double? liquidityBuy,
        double? liquiditySell)
    {
        // Every term is a headroom: a ceiling less what is already used, floored at zero. A missing
        // ceiling is not a zero headroom — it is a constraint this venue did not state, so it binds
        // nothing and drops out of the minimum.
        var openLeft = Headroom(maxOpenInterest, totalOi);
        var groupLeft = Headroom(groupMaxOi, groupOi);
        var walletLeft = maxWalletOi;

        var pairUsed = (longOi ?? 0) + (shortOi ?? 0);
        var pairLeft = Headroom(pairMaxOi, pairUsed);

        var sideLong = SideCap(groupMaxOi, groupOpenInterestPercentageP, maxLongOiP, longOi);
        var sideShort = SideCap(groupMaxOi, groupOpenInterestPercentageP, maxShortOiP, shortOi);

        return (
            Smallest(openLeft, groupLeft, walletLeft, Smallest(pairLeft, sideLong), liquidityBuy),
            Smallest(openLeft, groupLeft, walletLeft, Smallest(pairLeft, sideShort), liquiditySell));
    }

    /// <summary>A pair's own slice of its group's ceiling, on one side, less what that side already
    /// holds.</summary>
    private static double? SideCap(double? groupMaxOi, double? groupPct, double? sidePct, double? sideOi)
    {
        if (groupMaxOi is not { } max || groupPct is not { } gp || sidePct is not { } sp)
        {
            return null;
        }

        return Headroom(max * gp / 100d * (sp / 100d), sideOi ?? 0);
    }

    private static double? Headroom(double? ceiling, double? used) =>
        ceiling is { } c && double.IsFinite(c) ? Math.Max(c - (used ?? 0), 0) : null;

    /// <summary>The binding constraint. Nulls are absent constraints and are skipped; all-null means
    /// the venue stated no ceiling at all, which is null rather than zero.</summary>
    private static double? Smallest(params double?[] limits)
    {
        double? best = null;
        foreach (var limit in limits)
        {
            if (limit is { } l && double.IsFinite(l) && (best is not { } b || l < b))
            {
                best = l;
            }
        }

        return best;
    }
}

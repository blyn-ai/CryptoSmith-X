using CryptoSmithX.MarketData.Connectors.Kraken;

namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// Turning a venue's raw book levels into <see cref="Depth"/> when that venue quotes sizes in
/// CONTRACTS rather than coins.
///
/// <b>Why this exists rather than each adapter calling <see cref="DepthMath"/> itself.</b> The depth
/// bands are a NOTIONAL — quote currency inside a band of the mid — so they are computed in the
/// adapter and stored finished. Every other size on a contract-quoted venue is stored raw and
/// multiplied by <c>contract_multiplier</c> downstream, which means the multiplier is applied in two
/// different places for two different columns of the same row. Get it wrong here and the depth
/// columns are off by the contract size — a hundredfold on OKX, ten thousandfold on Gate and MEXC —
/// and nothing about the resulting number looks wrong: it sorts, it renders, it has the right shape.
///
/// Four venues need this and they are the four most likely to be copied from each other, so the
/// conversion is written once and named after what it is.
/// </summary>
public static class ContractBook
{
    /// <summary>
    /// Cumulative-notional depth from levels whose quantities are contract counts.
    ///
    /// <paramref name="contractMultiplier"/> is base-asset units per contract, as the instrument
    /// carries it. A multiplier of one is the coin-quoted case and passes straight through, so a
    /// venue that changes its mind about its own units does not need a different call here.
    /// </summary>
    public static Depth? Compute(
        IReadOnlyList<(double Price, double Contracts)> bids,
        IReadOnlyList<(double Price, double Contracts)> asks,
        double contractMultiplier,
        DateTimeOffset at)
    {
        if (!double.IsFinite(contractMultiplier) || contractMultiplier <= 0)
        {
            // No usable contract size means the notional cannot be formed at all. Null is "not
            // measured this frame", which is what the depth columns already know how to read — a
            // band computed against a guessed multiplier would not be.
            return null;
        }

        return DepthMath.Compute(Scale(bids, contractMultiplier), Scale(asks, contractMultiplier), at);
    }

    private static List<(double Price, double Qty)> Scale(
        IReadOnlyList<(double Price, double Contracts)> levels, double multiplier)
    {
        var scaled = new List<(double Price, double Qty)>(levels.Count);
        foreach (var (price, contracts) in levels)
        {
            scaled.Add((price, contracts * multiplier));
        }

        return scaled;
    }
}

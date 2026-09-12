using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// The venue's replacement for a book, read as one.
///
/// Avantis has no resting orders, but it will quote a cost for a size and a side, and that is the
/// same information a book carries — a price to buy, a price to sell, how far either goes before it
/// gets expensive, and where it stops. This class asks those questions and returns the answers in
/// the shapes the rest of the pipeline already speaks: a <see cref="Ticker"/>'s bid and ask, and a
/// <see cref="Depth"/>.
///
/// <b>What a reader must not lose.</b> These are quotes, not resting size. Nobody is standing there
/// offering them; the venue is promising to fill them right now. The column's badge is where that
/// distinction lives, and it is not optional.
///
/// <b>Cost.</b> Two requests per pair for the baseline, which is what the minute pass pays. The
/// curve is a bisection per threshold per side and costs an order of magnitude more, so it runs on
/// its own slower sweep — see <see cref="CurveAsync"/>. Measured on mainnet: 0.19 s per call,
/// 53.6 req/s at concurrency 16 with no throttling observed, and the status split identical at
/// concurrency 1, 8 and 16, which is what says the refusals are properties of pairs rather than of
/// load.
/// </summary>
public sealed class AvantisQuotes
{
    /// <summary>The thresholds the shared depth columns are defined at. Same three for every venue,
    /// because the column is a comparison.</summary>
    private static readonly double[] Thresholds = [10d, 25d, 50d];

    private readonly AvantisClient _client;

    public AvantisQuotes(AvantisClient client) => _client = client;

    /// <summary>
    /// Bid, ask and spread at the standard notional — the two requests the minute pass pays for.
    ///
    /// <paramref name="index"/> is the oracle price the venue itself quotes against, so the two
    /// prices are around it rather than around each other.
    /// </summary>
    public async Task<AvantisQuote> BaselineAsync(
        int pairIndex, double index, decimal multiplier, CancellationToken ct)
    {
        var size = AvantisQuoteMath.CoinSize(AvantisQuoteMath.StandardNotional, index, multiplier);
        if (size is not { } coinSize)
        {
            return AvantisQuote.None;
        }

        // Both sides in flight together: they are independent questions and the pass is on a clock.
        var longTask = _client.GetSpreadPctAsync(pairIndex, coinSize, isLong: true, ct);
        var shortTask = _client.GetSpreadPctAsync(pairIndex, coinSize, isLong: false, ct);
        await Task.WhenAll(longTask, shortTask);

        var (bid, ask) = AvantisQuoteMath.BidAsk(index, longTask.Result, shortTask.Result);
        return new AvantisQuote(
            Bid: bid,
            Ask: ask,
            SpreadBps: AvantisQuoteMath.Bps(bid, ask),
            SpreadLongPct: longTask.Result,
            SpreadShortPct: shortTask.Result,
            QuotedNotional: AvantisQuoteMath.StandardNotional,
            CoinSize: coinSize);
    }

    /// <summary>
    /// The whole curve: how much this venue will take on each side at 10, 25 and 50 bps, and the
    /// largest size it still quotes at all.
    ///
    /// Depths come back as cumulative notional in the quote asset, which is what the shared columns
    /// hold for every other venue — a size in base units would not be comparable down the column.
    /// The reach is expressed in bps from mid, the way <see cref="Depth.ReachBidBps"/> is defined,
    /// and the size that reach was found at rides along so the figure stays reproducible.
    ///
    /// One cost cache per sweep: the four bisections on a side overlap heavily near the middle, and
    /// a repeated probe should not be a repeated request.
    /// </summary>
    public async Task<(Depth? Depth, double? ReachBidSize, double? ReachAskSize)> CurveAsync(
        int pairIndex, double index, decimal multiplier, DateTimeOffset at, CancellationToken ct)
    {
        var seed = AvantisQuoteMath.CoinSize(AvantisQuoteMath.StandardNotional, index, multiplier);
        if (seed is not { } start)
        {
            return (null, null, null);
        }

        var bid = await SideAsync(pairIndex, index, start, isLong: false, ct);
        var ask = await SideAsync(pairIndex, index, start, isLong: true, ct);

        var depth = new Depth(
            Bid10Bps: Notional(bid.Depth[0], index),
            Ask10Bps: Notional(ask.Depth[0], index),
            Bid25Bps: Notional(bid.Depth[1], index),
            Ask25Bps: Notional(ask.Depth[1], index),
            Bid50Bps: Notional(bid.Depth[2], index),
            Ask50Bps: Notional(ask.Depth[2], index),
            At: at,
            Mid: index,
            // 0 rather than null when nothing was quotable: the column's own comment (0030) says a
            // measurement that happened and found nothing must stay distinguishable from a frame
            // where depth was never collected, and that second case is a null Depth entirely.
            ReachBidBps: bid.ReachBps ?? 0,
            ReachAskBps: ask.ReachBps ?? 0);

        return (depth, bid.ReachSize, ask.ReachSize);
    }

    private async Task<SideCurve> SideAsync(
        int pairIndex, double index, double seed, bool isLong, CancellationToken ct)
    {
        var seen = new Dictionary<double, double?>();

        async Task<double?> CostAsync(double size)
        {
            if (seen.TryGetValue(size, out var cached))
            {
                return cached;
            }

            var pct = await _client.GetSpreadPctAsync(pairIndex, size, isLong, ct);
            // Percent of the oracle on one side; the columns are defined in bps, so the two halves
            // of a round trip are not conflated here — this is the cost of THIS side only.
            var bps = pct is { } p && double.IsFinite(p) ? p * 100d : (double?)null;
            seen[size] = bps;
            return bps;
        }

        // Reach first: it bounds every threshold below it, and its own answer is one of the
        // columns rather than scaffolding.
        var reach = await SearchAsync(seed, CostAsync, _ => true, ct);

        var depths = new double?[Thresholds.Length];
        for (var i = 0; i < Thresholds.Length; i++)
        {
            var limit = Thresholds[i];
            depths[i] = await SearchAsync(seed, CostAsync, bps => bps <= limit, ct);
        }

        var reachBps = reach is { } r && seen.TryGetValue(r, out var cost) ? cost : null;
        return new SideCurve(depths, reach, reachBps);
    }

    /// <summary>The bisection itself: <see cref="AvantisQuoteMath.Bracket"/> decides where to look
    /// next, this only fetches and folds. A size the venue refused reads as "does not fit", which is
    /// what makes the same loop serve both the thresholds and the reach.</summary>
    private static async Task<double?> SearchAsync(
        double seed,
        Func<double, Task<double?>> costAsync,
        Func<double, bool> fits,
        CancellationToken ct)
    {
        var bracket = AvantisQuoteMath.Bracket.Start(seed);
        while (!bracket.Done)
        {
            ct.ThrowIfCancellationRequested();
            var probe = bracket.NextProbe;
            var cost = await costAsync(probe);
            bracket = bracket.Observe(probe, withinThreshold: cost is { } c && fits(c));
        }

        return bracket.Best;
    }

    /// <summary>A size in base units into the quote asset, at the same oracle price the quote was
    /// taken against. Never at a different price than the one the search used.</summary>
    private static double? Notional(double? coinSize, double index) =>
        coinSize is { } s && double.IsFinite(index) && index > 0 ? s * index : null;

    private readonly record struct SideCurve(double?[] Depth, double? ReachSize, double? ReachBps);
}

/// <summary>
/// One pair's executable quote at a stated size. <see cref="QuotedNotional"/> is part of the
/// reading, not metadata: the same venue quotes a different spread at a different size, so a figure
/// without its size is not a figure.
/// </summary>
public sealed record AvantisQuote(
    double? Bid,
    double? Ask,
    double? SpreadBps,
    double? SpreadLongPct,
    double? SpreadShortPct,
    double? QuotedNotional,
    double? CoinSize)
{
    /// <summary>No usable oracle, so no quote was asked for — distinct from a venue that was asked
    /// and refused, which comes back with the prices null but the notional stated.</summary>
    public static readonly AvantisQuote None = new(null, null, null, null, null, null, null);
}

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// The arithmetic that turns Avantis's quote endpoint into the figures the shared table's own
/// columns hold.
///
/// <b>What this venue answers, and why it belongs in the quote columns.</b> Avantis keeps no
/// resting book, and for a long time that was read here as "these columns are structurally empty".
/// It was wrong, and the measurement that settles it is a single anonymous call: the risk engine
/// quotes a spread for a SIZE and a SIDE. On ETH, mainnet, zero-address trader —
///
///   0.1 / 1 / 10 ETH   0.010000 %   both sides
///   100 ETH            0.016952 % long against 0.018355 % short
///   1 000 ETH          0.048176 % long against 0.043772 % short
///   5 000 ETH          0.2596 %  — that is 26.0 bps
///   50 000 ETH         SM004, no quote at all
///
/// That is a cost-of-size curve with a side-dependent skew and a hard end. A book answers exactly
/// the same questions with levels instead of a function, so a bid, an ask, a spread, a depth at a
/// threshold and a reach all exist here — they are simply computed rather than read off. What must
/// not be lost is that they are QUOTES, not resting orders, which is what the column's own badge
/// says.
///
/// <b>Why the size is a notional and not a quantity.</b> The column compares venues. A fixed base
/// quantity would ask BTC for a hundred times the money it asks of a memecoin, and the two numbers
/// would not be comparable down the column — which is the one thing this table is for.
/// </summary>
public static class AvantisQuoteMath
{
    /// <summary>
    /// The standard size every quote column is measured at: ten thousand of the quote asset per
    /// side. Written here rather than in a setting because it is part of what the column MEANS —
    /// the header prints it, and a figure quoted at a different size is a different figure.
    /// </summary>
    public const double StandardNotional = 10_000d;

    /// <summary>
    /// The notional turned into base units at the live oracle price, through the instrument's own
    /// multiplier. Null when there is no usable price: dividing by a missing oracle is how a
    /// request for "infinity ETH" reaches a venue.
    /// </summary>
    public static double? CoinSize(double notionalQuote, double indexPrice, decimal multiplier)
    {
        if (!double.IsFinite(indexPrice) || indexPrice <= 0 || multiplier <= 0)
        {
            return null;
        }

        var size = notionalQuote / (indexPrice * (double)multiplier);
        return double.IsFinite(size) && size > 0 ? size : null;
    }

    /// <summary>
    /// The two executable prices around the oracle. Each side carries ITS OWN measured cost — at
    /// 100 ETH the two differ, and that difference is the venue's skew, not noise. A side the
    /// engine would not quote (SM004, or a 403 on a shut market) stays absent: zero would read as
    /// a free fill.
    /// </summary>
    public static (double? Bid, double? Ask) BidAsk(
        double index, double? spreadLongPct, double? spreadShortPct)
    {
        if (!double.IsFinite(index) || index <= 0)
        {
            return (null, null);
        }

        // Long is what a BUYER pays, so it lifts the ask; short is what a SELLER receives, so it
        // presses the bid down. Percent, as the engine publishes it — hence /100.
        var ask = spreadLongPct is { } l && double.IsFinite(l) ? index * (1 + l / 100d) : (double?)null;
        var bid = spreadShortPct is { } s && double.IsFinite(s) ? index * (1 - s / 100d) : (double?)null;
        return (bid, ask);
    }

    /// <summary>
    /// The spread column's own figure: the distance between the two quotes over their mid, in basis
    /// points. Computed the same way a book venue's is, because that is the comparison the column
    /// exists to support — not the venue's stated <c>spread_p</c>, which is one input to the cost
    /// rather than the cost.
    /// </summary>
    public static double? Bps(double? bid, double? ask)
    {
        if (bid is not { } b || ask is not { } a || b <= 0 || a <= 0)
        {
            return null;
        }

        var mid = (a + b) / 2d;
        return mid > 0 ? (a - b) / mid * 10_000d : null;
    }

    /// <summary>
    /// A bisection over SIZE, used for both the depth thresholds and the reach: "the largest size
    /// this venue still quotes at or under this cost".
    ///
    /// <b>Why a bracket rather than a ladder of fixed sizes.</b> A ladder either misses thin markets
    /// entirely or spends a request on every rung of a deep one. The bracket starts at the standard
    /// notional's own size — a size we are quoting anyway — and walks outward only as far as the
    /// answer actually is.
    ///
    /// <b>Why the answer is always a size that was quoted.</b> <see cref="Best"/> only ever holds a
    /// probe that came back within the threshold. Returning the midpoint the search stopped on
    /// would name a size the venue may have refused.
    ///
    /// Every step is a live round trip, so the search is bounded twice: by
    /// <see cref="MaxSteps"/> and by <see cref="Tolerance"/>. Measured, the endpoint answers in
    /// 0.19 s and sustains 53.6 req/s with no throttling, so the bound is about restraint rather
    /// than capacity.
    /// </summary>
    public readonly record struct Bracket
    {
        /// <summary>How close the bracket has to be before the answer stops being worth a request.
        /// A RATIO, not a difference: the answers span four orders of magnitude across this venue's
        /// own listings — measured, $16 563 of depth on PENGU against $5 120 000 on ETH — and an
        /// absolute tolerance would be pointless at one end and ruinous at the other.</summary>
        public const double Tolerance = 0.01d;

        /// <summary>
        /// How fast the search reaches outward while it has no ceiling yet.
        ///
        /// FOUR, not two, and this was found by running the search against the live venue rather
        /// than by reasoning. Doubling from a ten-thousand-dollar seed to ETH's real reach of about
        /// $41M takes twelve probes on its own; the step bound then fired during the reach phase and
        /// every answer came back as an exact power of two times the seed — a lower bound wearing a
        /// measurement's clothes. Quadrupling gets there in six and leaves the rest of the budget
        /// for actually converging.
        /// </summary>
        private const double Growth = 4d;

        /// <summary>A hard stop independent of the tolerance, so a pathological venue response can
        /// never turn one instrument's sweep into an unbounded loop. Eighteen is what the geometry
        /// needs: six to bracket the widest real answer, then seven to close a four-fold bracket to
        /// one percent, with a margin.</summary>
        public const int MaxSteps = 18;

        /// <summary>Below this fraction of the opening size the search gives up rather than keep
        /// halving. A thousandth of the standard notional is ten dollars: a market that will not
        /// quote ten dollars has no depth at this threshold, and finding that out must not cost a
        /// full fourteen round trips on every sweep — there are fifty-one pairs, three thresholds
        /// and two sides, so the wasted requests are counted in thousands, not units.
        ///
        /// Deliberately coarser than it could be. At 1e-4 the halving takes exactly fourteen steps
        /// and the guard never fires before <see cref="MaxSteps"/> does, which is a guard in name
        /// only; at 1e-3 it stops at ten and actually saves the four.</summary>
        private const double Negligible = 1e-3;

        private readonly double _low;
        private readonly double _high;
        private readonly double _seed;
        private readonly int _steps;

        private Bracket(
            double low, double high, double seed, double? best, double nextProbe, int steps, bool done)
        {
            _low = low;
            _high = high;
            _seed = seed;
            _steps = steps;
            Best = best;
            NextProbe = nextProbe;
            Done = done;
        }

        /// <summary>The largest size observed to fit, or null while none has.</summary>
        public double? Best { get; }

        /// <summary>The size to ask about next. Meaningless once <see cref="Done"/>.</summary>
        public double NextProbe { get; }

        public bool Done { get; }

        /// <summary>Opens the search at a size we are quoting anyway — no request is wasted on
        /// finding somewhere to start.</summary>
        public static Bracket Start(double seed)
        {
            var s = double.IsFinite(seed) && seed > 0 ? seed : 1d;
            // high = 0 means "no upper bound found yet": the search doubles until something is
            // too dear, and only then bisects.
            return new Bracket(low: 0d, high: 0d, seed: s, best: null, nextProbe: s, steps: 0, done: false);
        }

        /// <summary>Folds one answer in and produces the next question.</summary>
        public Bracket Observe(double probe, bool withinThreshold)
        {
            var steps = _steps + 1;
            var low = _low;
            var high = _high;
            var best = Best;

            if (withinThreshold)
            {
                low = probe;
                best = best is { } b && b > probe ? b : probe;
            }
            else
            {
                high = probe;
            }

            // No ceiling yet: reach outward geometrically rather than guessing one.
            if (high <= 0)
            {
                var next = (low > 0 ? low : probe) * Growth;
                return new Bracket(low, high, _seed, best, next, steps, steps >= MaxSteps);
            }

            // No floor yet: close in on zero from above, the thin-market case.
            if (low <= 0)
            {
                var next = high / Growth;
                var exhausted = steps >= MaxSteps
                    || next <= 0 || !double.IsFinite(next)
                    || next < _seed * Negligible;
                return new Bracket(low, high, _seed, best, next, steps, exhausted);
            }

            // GEOMETRIC midpoint. An arithmetic one would spend most of its steps in the top half
            // of a bracket that spans a factor of four, and the tolerance is a ratio.
            var tight = high <= low * (1 + Tolerance);
            var mid = Math.Sqrt(low * high);
            return new Bracket(low, high, _seed, best, mid, steps, tight || steps >= MaxSteps);
        }
    }
}

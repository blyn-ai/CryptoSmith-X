using CryptoSmithX.Arena.Models;

namespace CryptoSmithX.Arena.Data;

/// <summary>
/// The maximum each comparative bar is drawn against.
///
/// Rule 11 gives a bar to the columns that keep no hourly rollup — the two sizes, turnover, and the
/// three depth bands as mirrored pairs — and says the bar runs "against the largest venue on
/// screen". This class decides what "on screen" means, and the answer is not "every row".
///
/// <b>A bar is a comparison, so it obeys the verdict's scope.</b> Blueprint §6 settled that
/// anything denominated in the quote asset ranks only against rows sharing that quote, because the
/// fold guarantees Kraken's book in USD sits beside WEEX's in USDT on one page. That argument is
/// about the act of comparing, not about the chip: a turnover bar drawn 40% as long as its
/// neighbour is the same claim as a WORST chip, made quietly. So turnover and the three depth bands
/// scale per quote asset, and the sizes — carried through <c>contract_multiplier</c> into base-asset
/// units, exactly as <see cref="Verdicts"/> does — scale across the whole page.
///
/// Rejected: one maximum per column across every row regardless of quote. It is what the design
/// system's prose literally says and it draws a USD book against a USDT book, which is the mistake
/// rule 7's scope exists to prevent, in a form the reader cannot even see well enough to distrust.
///
/// Rejected: scaling the sizes on their raw figures. One unit of quantity is not one coin —
/// 1000PEPE and kPEPE both carry a multiplier of 1000 (0001) — so raw bars would rank contract
/// sizes rather than books.
///
/// A group of one gets no scale at all: a bar at full width against itself says "largest", and it
/// is the only row there. Same rule as the verdicts, same reason.
///
/// <b>And a degraded row is not in the group.</b> Same rule again, and this time literally the same
/// code — <see cref="Verdicts.TakesPart"/> — rather than a second copy of it here. The first repair
/// of the chip left this class taking rows with no ages at all, so a book nobody had observed in
/// three days still supplied the column maximum and still drew its own bar at full length: the page
/// refused to say "largest size on the page" in the loud channel and said it in the quiet one, about
/// the same cell, in the same column, and scaled every living venue's bar against a quotation it had
/// already declared dead. That is why <see cref="Compute"/> takes the rows WITH their ages.
/// </summary>
public sealed class ColumnScales
{
    public static readonly ColumnScales Empty = new(new Dictionary<(int, PairColumn), double>());

    private readonly IReadOnlyDictionary<(int InstrumentId, PairColumn Column), double> _maxima;

    private ColumnScales(IReadOnlyDictionary<(int, PairColumn), double> maxima) => _maxima = maxima;

    /// <summary>
    /// The maximum this row's bar in this column is drawn against, or null when the column carries
    /// no bar for this row — nothing comparable beside it, or nothing measured anywhere in its
    /// group. Null means the slot is reserved and left empty, never a bar of zero length: a bar
    /// against nothing would say "smallest", which is a rank, and there is no rank here.
    /// </summary>
    public double? Of(int instrumentId, PairColumn column) =>
        _maxima.TryGetValue((instrumentId, column), out var m) ? m : null;

    /// <summary>No scope of its own: it asks <see cref="Verdicts.Scope"/>. A bar and a chip on one
    /// cell describing two different comparisons is the drift this removes the possibility of.</summary>
    private sealed record Spec(PairColumn Column, Func<PairVenueRow, IEnumerable<double?>> Values);

    /// <summary>
    /// The columns that carry a bar, and nothing else.
    ///
    /// Absent on purpose, because rule 11 was corrected against the rendered page and this list is
    /// that correction: bid, ask, spread, last, funding and open interest carry an hourly LINE
    /// rather than a bar — a bar against other venues would be a second comparison stacked on a
    /// column that already has one — and mark and index carry neither, because they are quoted
    /// rather than accumulated and ranking them would rank numbers that are not competing.
    ///
    /// The depth bands are here because their mirrored bar shows the two sides against each other,
    /// and a one-sided book — the thing that column exists to catch — only reads as lopsided if
    /// both halves are measured on one scale. Both sides of every venue in the group feed the
    /// maximum, which is why the value selector returns a sequence rather than a figure.
    /// </summary>
    private static readonly Spec[] Specs =
    [
        new(PairColumn.BidSize, r => [Scaled(r.BidSize, r)]),
        new(PairColumn.AskSize, r => [Scaled(r.AskSize, r)]),
        new(PairColumn.Turnover24h, r => [r.Turnover24h]),
        new(PairColumn.Depth10, r => [r.DepthBid10, r.DepthAsk10]),
        new(PairColumn.Depth25, r => [r.DepthBid25, r.DepthAsk25]),
        new(PairColumn.Depth50, r => [r.DepthBid50, r.DepthAsk50])
    ];

    public static ColumnScales Compute(IReadOnlyList<VenueRowModel> rows)
    {
        if (rows.Count < 2)
        {
            return Empty;
        }

        var maxima = new Dictionary<(int, PairColumn), double>();

        foreach (var spec in Specs)
        {
            var groups = Verdicts.Scope(spec.Column) == VerdictScope.PerQuoteAsset
                ? rows.GroupBy(r => r.Row.QuoteAsset, StringComparer.Ordinal)
                : rows.GroupBy(_ => "", StringComparer.Ordinal);

            foreach (var group in groups)
            {
                // Dropped before the count, exactly as the verdict drops it, and for the same
                // reason: two rows of which one is dead are not two rows. The survivor's bar would
                // then be drawn against itself at full width, which says "largest" about a group
                // of one.
                var members = group.Where(v => Verdicts.TakesPart(v, spec.Column)).ToList();
                if (members.Count < 2)
                {
                    continue;
                }

                var measured = members
                    .SelectMany(v => spec.Values(v.Row))
                    .Where(v => v is { } x && !double.IsNaN(x) && !double.IsInfinity(x) && x > 0)
                    .Select(v => v!.Value)
                    .ToList();

                if (measured.Count == 0)
                {
                    continue;
                }

                var max = measured.Max();
                foreach (var member in members)
                {
                    maxima[(member.Row.InstrumentId, spec.Column)] = max;
                }
            }
        }

        return new ColumnScales(maxima);
    }

    private static double? Scaled(double? quantity, PairVenueRow row) =>
        quantity is { } q && row.ContractMultiplier > 0 ? q * row.ContractMultiplier : null;
}

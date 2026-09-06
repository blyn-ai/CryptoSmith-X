using CryptoSmithX.Arena.Models;

namespace CryptoSmithX.Arena.Data;

/// <summary>Which columns of the comparison carry a rank at all.</summary>
/// <remarks>
/// The list is rule 7's list, verbatim: highest bid, lowest ask, narrowest spread, largest size /
/// turnover / open interest / depth. What is NOT here is as deliberate as what is.
///
/// * <b>Funding.</b> Not ranked, at all, in any scope. Which direction is good depends on which
///   side of the trade the reader is on, and the page does not know that. Worse, the intervals
///   differ inside a single venue — weex carries 4 hourly instruments, 480 four-hourly and 539
///   eight-hourly — so even the sign of "better" is not shared across the column. The rate is
///   printed with its own interval beside it and the per-day figure is labelled as normalised.
///   The rendered mock's legend does not list funding under Best/Worst either.
/// * <b>Last, mark, index.</b> Quoted rather than accumulated. Rule 11 refuses them even a
///   comparison bar for the same reason: ranking them would rank numbers that are not competing.
/// </remarks>
public enum PairColumn
{
    Bid,
    Ask,
    SpreadBps,
    BidSize,
    AskSize,
    Turnover24h,
    OpenInterest,
    Depth10,
    Depth25,
    Depth50
}

/// <summary>Which rows a column is allowed to be ranked against.</summary>
public enum VerdictScope
{
    /// <summary>
    /// Only rows quoting in the same asset. Every figure expressed in the quote currency lives
    /// here: bid, ask, turnover and all three depth bands.
    ///
    /// The fold is a decided feature, so a single page is guaranteed to hold Kraken's book in USD
    /// beside WEEX's in USDT. An acid-green BEST chip over a comparison of two different currencies
    /// is not a footnote-sized problem — it is a false statement, and a footnote does not fix it.
    /// </summary>
    PerQuoteAsset,

    /// <summary>
    /// Every row on the page. Only quantities with no currency in them qualify: the spread in basis
    /// points, sizes and open interest carried through contract_multiplier into base-asset units.
    /// </summary>
    WholePair
}

public enum Verdict
{
    None,
    Best,
    Worst
}

/// <summary>The rank of every cell that has one. Cells with no rank are simply absent.</summary>
public sealed class VerdictTable
{
    public static readonly VerdictTable Empty = new(new Dictionary<(int, PairColumn), Verdict>());

    private readonly IReadOnlyDictionary<(int InstrumentId, PairColumn Column), Verdict> _marks;

    internal VerdictTable(IReadOnlyDictionary<(int InstrumentId, PairColumn Column), Verdict> marks) =>
        _marks = marks;

    public Verdict Of(int instrumentId, PairColumn column) =>
        _marks.TryGetValue((instrumentId, column), out var v) ? v : Verdict.None;

    /// <summary>How many cells carry a mark. Exists for the tests and for a page that wants to say
    /// nothing was comparable rather than draw an empty legend.</summary>
    public int Count => _marks.Count;
}

/// <summary>
/// BEST and WORST across the venues on one comparison page.
///
/// Two rules decide everything here and both come from the design system's rule 7.
///
/// <b>Both ends or neither.</b> A table that only ever praises tells the reader half of what it
/// knows. So a column is marked at both ends or not at all: fewer than two comparable rows, or
/// every comparable row holding the same value, and the column goes unmarked. Marking a two-row tie
/// as best and worst would invent a difference that is not in the data.
///
/// <b>Scope.</b> Ranking is per <see cref="VerdictScope"/>, and the reason is the fold: BTC/USD on
/// this page means BTC/USDT on one venue and BTC/USD on another. Anything denominated in the quote
/// ranks only against rows sharing that quote.
///
/// A missing figure has no rank, ever — it neither wins nor loses. A dash means not measured, and
/// letting it lose a column would be exactly the substitution of a dash for a zero that the whole
/// surface exists to refuse.
///
/// <b>A degraded figure has no rank either.</b> Rule 7's "verdicts do not fade with the row" is
/// about the chip's OPACITY — the mark keeps its ink while the figure under it ghosts, because the
/// rank was computed across venues rather than read off the call this row is waiting on. It is not
/// a licence to rank a quotation the page has already declared dead. <c>degraded</c> is this
/// surface's own word, printed in the notebar as "it has stopped meaning anything", and a BEST is a
/// claim about the market NOW: an acid-green wash on a book nobody has observed in three days tells
/// the reader that venue has the tightest spread on the page, from a row whose every age line says
/// the figure means nothing. So the candidate list drops it, PER CALL — the price call, the
/// open-interest call and the depth sweep are three clocks, and a row whose depth sweep died
/// yesterday still has a two-second bid that ranks. △ is NOT the line: past its window a figure has
/// stopped being graded, not stopped being true, and dropping everything past one window would
/// silently empty most columns on a venue whose pass is wide.
///
/// <b>A crossed book has no rank in the spread column either</b>, and that one is not about age at
/// all: the spread of a book whose bid stands above its ask is a negative number the page prints as
/// it stands, and a column that ranks low-is-best with no floor crowned it TIGHT. See
/// <see cref="Crossed"/>.
///
/// Both ends or neither survives BOTH exclusions, because they are applied before the count: if
/// dropping the degraded and the crossed rows leaves one comparable figure, the column goes unmarked
/// rather than crowning the only survivor.
///
/// <b>The same exclusion governs the bar.</b> <see cref="ColumnScales"/> asks this class, through
/// <see cref="TakesPart"/>, rather than deciding it again — a turnover bar drawn 40% as long as its
/// neighbour is the same claim as a WORST chip, made quietly, so a figure that is not fit to be
/// ranked is not fit to set the column maximum or to draw its own bar either. Two copies of the
/// rule would have split exactly there, and did: the first repair dropped the degraded row from the
/// chip and left it supplying the maximum every live bar on the page was scaled against.
///
/// <b>The clock keeps moving after the render, and the client cannot rank.</b> A row crosses the
/// twelfth window while the tab is open, and this table — computed once, per request — is then a
/// judgement about an instant that has passed. The answer is NOT to re-run this arithmetic in
/// JavaScript: see the withdrawal block in <c>wwwroot/arena-ages.js</c>, which argues the three
/// candidate designs and takes the one that needs no ranking on the client at all. What that
/// design needs FROM here is <see cref="RankGroup"/> — the set of cells one comparative claim is
/// made across — so the client can retract a whole claim without being able to invent one.
/// </summary>
public static class Verdicts
{
    /// <summary>Which of the row's three calls wrote this column's figure — and therefore which
    /// clock decides whether the figure is still worth ranking.</summary>
    private enum Call
    {
        Price,
        OpenInterest,
        Depth
    }

    /// <param name="Shown">
    /// The figure AS THE CELL PRINTS IT, rounded to that row's own display precision, and not the
    /// raw double behind it. The page prints a price to <c>price_step</c>, a size to the quantity
    /// step carried into base units and the spread to three decimals; ranking the unrounded values
    /// hands BEST to one cell and WORST to another over a difference the display never shows —
    /// two figures rendering as the same characters, one washed acid green and the other magenta.
    /// The tie guard below is the whole reason this rounds: "both ends or neither" has to mean
    /// both ends of a difference the reader can see.
    /// </param>
    private sealed record Spec(
        PairColumn Column,
        VerdictScope Scope,
        bool HighIsBest,
        Call Wrote,
        Func<PairVenueRow, double?> Shown);

    private static readonly Spec[] Specs =
    [
        // Prices and everything else denominated in the quote asset: same-quote rows only.
        new(PairColumn.Bid, VerdictScope.PerQuoteAsset, HighIsBest: true, Call.Price,
            r => Shown(r.BidPrice, Format.PriceDecimals(r))),
        new(PairColumn.Ask, VerdictScope.PerQuoteAsset, HighIsBest: false, Call.Price,
            r => Shown(r.AskPrice, Format.PriceDecimals(r))),
        new(PairColumn.Turnover24h, VerdictScope.PerQuoteAsset, HighIsBest: true, Call.Price,
            r => Shown(r.Turnover24h, 0)),

        // Depth bands are notional sums in the quote asset (0001 on depth_bid_10bps), so they carry
        // the currency with them and rank inside it. Each SIDE is rounded before the sum, because
        // each side is a printed number: two rows showing the same two figures must sum to the same
        // total, and rounding the sum instead could separate them by one unit the reader cannot see.
        new(PairColumn.Depth10, VerdictScope.PerQuoteAsset, HighIsBest: true, Call.Depth,
            r => ShownDepth(r.DepthBid10, r.DepthAsk10)),
        new(PairColumn.Depth25, VerdictScope.PerQuoteAsset, HighIsBest: true, Call.Depth,
            r => ShownDepth(r.DepthBid25, r.DepthAsk25)),
        new(PairColumn.Depth50, VerdictScope.PerQuoteAsset, HighIsBest: true, Call.Depth,
            r => ShownDepth(r.DepthBid50, r.DepthAsk50)),

        // Quote-free, so the whole page competes. Three decimals, which is what the cell prints.
        new(PairColumn.SpreadBps, VerdictScope.WholePair, HighIsBest: false, Call.Price,
            r => Shown(r.SpreadBps, SpreadDecimals)),

        // Sizes and open interest are in units of quantity, and one unit is not one coin: 1000PEPE
        // and kPEPE both carry a multiplier of 1000 (0001 on contract_multiplier). Multiplying is
        // what makes them the same measurement across venues; comparing the raw numbers would rank
        // contract sizes rather than books.
        new(PairColumn.BidSize, VerdictScope.WholePair, HighIsBest: true, Call.Price,
            r => Shown(Scaled(r.BidSize, r), Format.QuantityDecimals(r))),
        new(PairColumn.AskSize, VerdictScope.WholePair, HighIsBest: true, Call.Price,
            r => Shown(Scaled(r.AskSize, r), Format.QuantityDecimals(r))),

        // Open interest is the one ranked column on the open-interest call's own clock, which is why
        // the call travels with the spec rather than being assumed to be the price call.
        new(PairColumn.OpenInterest, VerdictScope.WholePair, HighIsBest: true, Call.OpenInterest,
            r => Shown(Scaled(r.OpenInterest, r), Format.QuantityDecimals(r)))
    ];

    /// <summary>Decimals the spread cell prints to. Beside the specs rather than inside them because
    /// <c>RowCells.Spread</c> prints the same number and the two must not drift apart.</summary>
    public const int SpreadDecimals = 3;

    private static readonly IReadOnlyDictionary<PairColumn, Spec> ByColumn =
        Specs.ToDictionary(s => s.Column);

    /// <summary>
    /// Which rows this column is ranked against. Read from the spec above rather than restated,
    /// because <see cref="ColumnScales"/> draws its bars across the same set and the two grouping
    /// them differently would put a chip and a bar on the same cell describing two different
    /// comparisons.
    /// </summary>
    public static VerdictScope Scope(PairColumn column) => ByColumn[column].Scope;

    /// <summary>
    /// Whether this row's figure in this column may take part in a comparison at all — the chip and
    /// the bar together, because they are the same claim in two channels.
    ///
    /// Two reasons a figure is kept out, and they are different kinds of reason.
    ///
    /// <b>The call that WROTE it has gone degraded.</b> Not the row: three calls, three clocks, and
    /// a venue whose depth sweep died yesterday still has a two-second bid that ranks. This is a
    /// claim about the age of the figure.
    ///
    /// <b>The spread column, on a crossed book.</b> This is a claim about the figure itself, and it
    /// applies however fresh the quotation is. See <see cref="Crossed"/>.
    /// </summary>
    public static bool TakesPart(VenueRowModel v, PairColumn column) =>
        !Degraded(v, ByColumn[column].Wrote)
        && !(column == PairColumn.SpreadBps && Crossed(v.Row));

    /// <summary>
    /// Whether this venue's book is crossed as quoted: its bid stands at or above its ask.
    ///
    /// <b>Why it is not ranked, rather than clamped or ignored.</b>
    /// <see cref="PairVenueRow.SpreadBps"/> returns a NEGATIVE number here on purpose — 0001 says a
    /// crossed book is written as it stands, because it is a fact about that venue at that instant —
    /// and the spread spec ranks low-is-best with no floor. So the most broken quotation on the page
    /// took TIGHT, in the acid wash rule 6 reserves for the loudest note on the surface, and by
    /// occupying the good end it pushed an ordinary two-dollar book onto WIDE. One broken row, two
    /// false statements, and the second one libels a venue that did nothing.
    ///
    /// A crossed book is not tight, and it is also not nothing. Rejected: flooring the figure at
    /// zero and ranking it anyway — that is the page rewriting a measurement, the act this whole
    /// surface exists to refuse, and it hands the crossed book a tie for the best spread rather than
    /// the win. Rejected: dropping the whole ROW from every column — bid and ask are each still that
    /// venue's own quotation and the highest bid on the page is still the highest bid; what is not
    /// meaningful is the DIFFERENCE between them, and that is one column. Rejected: dropping it
    /// silently, which leaves a spread cell with no mark and no reason, the same hole the degraded
    /// exclusion had to be given a sentence in the notebar to close. It leaves the comparison and
    /// wears its own word for why (<see cref="Models.SpreadBand.Crossed"/>).
    ///
    /// It is the raw comparison and not the printed one, unlike everything else in this file. The
    /// rest rounds because it RANKS, and two figures rendering as the same characters must not be
    /// ranked apart; this decides whether the figure means anything at all, and it is the same
    /// condition, on the same two doubles, that makes <see cref="PairVenueRow.SpreadBps"/> negative.
    /// Rounding here would let the sign of the spread and the reason for the chip disagree.
    ///
    /// A LOCKED book — bid exactly at the ask — is NOT crossed and is not excluded. Its spread is
    /// zero, and a zero is an observation: it is the tightest a book can be, and it wins TIGHT
    /// because it earned it. Only bid ABOVE ask is impossible as quoted.
    /// </summary>
    public static bool Crossed(PairVenueRow row) =>
        row.BidPrice is { } bid && row.AskPrice is { } ask && bid > ask;

    /// <summary>
    /// The set of cells one comparative claim is made across: this column, within the rows its
    /// scope allows. Two cells carrying the same key are two ends of one statement — "this is the
    /// largest of these" — and a page may keep or drop that statement, but never half of it.
    ///
    /// It exists for the client. <c>arena-ages.js</c> cannot recompute a rank and must not try, so
    /// what it is given instead is the grouping: when a call under a marked cell goes degraded
    /// after the render, the whole group's marks come off together and rule 7's "both ends or
    /// neither" survives an event the server never saw. The key is DERIVED from the spec — column
    /// plus the quote asset where the scope is per-quote, and column alone where the whole page
    /// competes — so a scope changed above changes the grouping the client uses in the same edit.
    /// A hand-written key in the view is the version of this that drifts.
    /// </summary>
    public static string RankGroup(PairVenueRow row, PairColumn column) =>
        Scope(column) == VerdictScope.PerQuoteAsset
            ? column + ":" + row.QuoteAsset
            : column + ":";

    public static VerdictTable Compute(IReadOnlyList<VenueRowModel> rows)
    {
        if (rows.Count < 2)
        {
            // One venue is not a comparison. Marking its only row BEST would be a ranking against
            // nothing, and rule 7 forbids one end without the other in any case.
            return VerdictTable.Empty;
        }

        var marks = new Dictionary<(int InstrumentId, PairColumn Column), Verdict>();

        foreach (var spec in Specs)
        {
            var groups = spec.Scope == VerdictScope.PerQuoteAsset
                ? rows.GroupBy(r => r.Row.QuoteAsset, StringComparer.Ordinal)
                : rows.GroupBy(_ => "", StringComparer.Ordinal);

            foreach (var group in groups)
            {
                MarkOne(marks, spec, group);
            }
        }

        return new VerdictTable(marks);
    }

    private static void MarkOne(
        Dictionary<(int, PairColumn), Verdict> marks, Spec spec, IEnumerable<VenueRowModel> group)
    {
        var candidates = group
            // Dropped before the count, not after: rule 7 is "both ends or neither", so a column
            // left with one live figure goes unmarked rather than crowning the survivor.
            .Where(v => TakesPart(v, spec.Column))
            .Select(v => (v.Row.InstrumentId, Value: spec.Shown(v.Row)))
            .Where(x => x.Value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
            .Select(x => (x.InstrumentId, Value: x.Value!.Value))
            .ToList();

        if (candidates.Count < 2)
        {
            return;
        }

        var high = candidates.Max(c => c.Value);
        var low = candidates.Min(c => c.Value);
        if (high == low)
        {
            // Everything comparable prints the same figure. There is no best and no worst here, and
            // saying otherwise would put a rank on a difference the page does not show.
            return;
        }

        var bestValue = spec.HighIsBest ? high : low;
        var worstValue = spec.HighIsBest ? low : high;

        // Ties at an end are all marked. Two venues genuinely showing the same top bid are both the
        // best bid on the page, and picking one of them would be an ordering the data does not have.
        foreach (var c in candidates)
        {
            if (c.Value == bestValue)
            {
                marks[(c.InstrumentId, spec.Column)] = Verdict.Best;
            }
            else if (c.Value == worstValue)
            {
                marks[(c.InstrumentId, spec.Column)] = Verdict.Worst;
            }
        }
    }

    /// <summary>
    /// Whether the call that wrote this column's figure has stopped meaning anything on this row.
    ///
    /// Per call and against that call's OWN window, the same arithmetic the age line and the venue
    /// strip use — a fifty-minute-old depth sweep is degraded on a venue that sweeps in six minutes
    /// and perfectly normal on one that sweeps daily. A call with no stated cadence is never
    /// degraded: not knowing how often we look is not knowing that the figure is dead, and refusing
    /// it a rank on that basis would be the page inventing the window it exists to report.
    /// </summary>
    private static bool Degraded(VenueRowModel v, Call call) => call switch
    {
        Call.OpenInterest => Freshness.Degraded(v.Ages.OpenInterestSeconds, v.Windows.OpenInterestSeconds),
        Call.Depth => Freshness.Degraded(v.Ages.DepthSeconds, v.Windows.DepthSeconds),
        _ => Freshness.Degraded(v.Ages.PriceSeconds, v.Windows.PriceSeconds)
    };

    /// <summary>A figure quantised to the decimals its cell prints it to. Away from zero, which is
    /// what <c>ToString("N…")</c> does, so the number ranked here is the number rendered.</summary>
    private static double? Shown(double? value, int decimals) =>
        value is { } v && !double.IsNaN(v) && !double.IsInfinity(v)
            ? Math.Round(v, decimals, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>Both printed sides of a depth band, each rounded as printed, then summed. Null
    /// unless both were measured — a band with one side missing is a partial measurement and not a
    /// smaller book.</summary>
    private static double? ShownDepth(double? bid, double? ask) =>
        Shown(bid, 0) is { } b && Shown(ask, 0) is { } a ? b + a : null;

    private static double? Scaled(double? quantity, PairVenueRow row) =>
        quantity is { } q && row.ContractMultiplier > 0 ? q * row.ContractMultiplier : null;
}

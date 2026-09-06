using CryptoSmithX.Arena.Data;

namespace CryptoSmithX.Arena.Models;

/// <summary>One closed hourly bar, exactly the columns the surface draws.</summary>
/// <remarks>
/// Volume is not read. The design system labels the candle panel <c>PRICE ONLY</c>, and
/// <c>market_candle.volume</c> is populated on 76.4% of bars (RULE-CHANGES.md §4) — a histogram
/// under seven panels where roughly a quarter of the bars are silently zero-height would draw an
/// absence as a measurement of nothing traded. Whether the panel should carry volume is recorded as
/// an open product question, not a rendering detail, and it is not settled here.
///
/// <see cref="BarCount"/> is kept because it is the honest caveat on the bar: below 60 the hour was
/// only partly covered by 1m bars (0001), and the panel header says how many such hours it drew
/// rather than pretending the line is continuous.
/// </remarks>
public sealed record CandleRow(
    int InstrumentId,
    DateTime OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    short BarCount);

/// <summary>
/// One instrument's hourly bars on the page's shared window list.
///
/// <see cref="Bars"/> is index-aligned with <see cref="Windows"/> and holds null where that venue
/// has no bar for that hour. The null is the whole point: it keeps the gap on the axis instead of
/// letting the neighbouring bars slide together, so a venue that stopped quoting looks stopped.
/// </summary>
public sealed record CandleSeries(IReadOnlyList<DateTime> Windows, IReadOnlyList<CandleRow?> Bars)
{
    public static readonly CandleSeries Empty = new([], []);

    /// <summary>The closes, still index-aligned and still holding nulls at the gaps. This is the
    /// series the bid, ask and last sparklines are drawn from — one price history, shown under the
    /// three figures that sit on it.</summary>
    public IReadOnlyList<double?> Closes => Bars.Select(b => b?.Close).ToList();

    public int Present => Bars.Count(b => b is not null);

    /// <summary>Hours the rollup covered only partly — fewer 1m bars than minutes in the hour. Said
    /// out loud in the panel header rather than smoothed into the line.</summary>
    public int Partial => Bars.Count(b => b is { BarCount: < CandleStore.TimeframeMinutes });

    public double? Low => Bars.Where(b => b is not null).Select(b => b!.Low).DefaultIfEmpty().Min() is var lo
        && Bars.Any(b => b is not null) ? lo : null;

    public double? High => Bars.Any(b => b is not null)
        ? Bars.Where(b => b is not null).Max(b => b!.High)
        : null;
}

/// <summary>One aggregated hour of microstructure, exactly the columns the four non-price
/// sparklines are drawn from.</summary>
/// <remarks>
/// Every one of the five figures is nullable in the schema and stays nullable here, because
/// <c>MetricHour.Aggregate</c> writes null rather than zero when an hour held no valid measurement
/// — a crossed book leaves the spread out of the average, a missing depth reading leaves the depth
/// out of its own. A zero there would read as "the spread was flat", not "unknown". Rule 8 is the
/// same rule one layer down, and honouring it here is what lets a gap in a line stay a gap.
///
/// <see cref="SnapshotCount"/> is the hour's own caveat: how many snapshots it was averaged from.
/// </remarks>
public sealed record MetricHourRow(
    int InstrumentId,
    DateTime HourTime,
    double? SpreadBpsAvg,
    double? FundingRateLast,
    double? OpenInterestLast,
    double? DepthBid25BpsAvg,
    double? DepthAsk25BpsAvg,
    short SnapshotCount);

/// <summary>
/// One instrument's hourly microstructure on the page's shared window list — the four series rule
/// 11 gives to spread, funding, open interest and depth 25bps.
///
/// Index-aligned with <see cref="Windows"/> and holding null at every hour that venue has no row
/// for, on the same argument as <see cref="CandleSeries"/>: the null keeps the gap on the axis, so
/// a venue that stopped being rolled up looks stopped instead of having its remaining points slide
/// together into an unbroken line.
/// </summary>
public sealed record MetricHourSeries(IReadOnlyList<DateTime> Windows, IReadOnlyList<MetricHourRow?> Hours)
{
    public static readonly MetricHourSeries Empty = new([], []);

    /// <summary>Average spread in bps per hour — the line under the figure the ticker call wrote.</summary>
    public IReadOnlyList<double?> Spread => Hours.Select(h => h?.SpreadBpsAvg).ToList();

    /// <summary>The last funding rate observed in each hour. A level, not a flow, which is why the
    /// rollup takes the last observation rather than an average.</summary>
    public IReadOnlyList<double?> Funding => Hours.Select(h => h?.FundingRateLast).ToList();

    /// <summary>
    /// The last open interest observed in each hour, in the venue's OWN units.
    ///
    /// The cell above it prints base units — the venue's figure through <c>contract_multiplier</c>
    /// — and this series is not multiplied to match. It does not need to be and it must not pretend
    /// to: a sparkline is normalised between its own minimum and maximum, so a constant factor
    /// cannot move a single pixel of it. Multiplying here would be arithmetic performed for
    /// appearances, on a number nobody reads off this line.
    /// </summary>
    public IReadOnlyList<double?> OpenInterest => Hours.Select(h => h?.OpenInterestLast).ToList();

    /// <summary>
    /// Depth at 25bps per hour, as the total resting on both sides.
    ///
    /// <b>Why the sum, and why only when both sides were measured.</b> Rule 11 gives this column a
    /// line AND a mirrored bar: the bar already says how the book is split between bid and ask right
    /// now, so the line's job is the other question — how deep the book was over the hour. The sum
    /// is that number, it is the two halves of the mirror added up rather than a third quantity, and
    /// it is what "depth at 25bps" means when it is said without a side.
    ///
    /// An hour with one side missing is a gap, never the side that is present: half a book plotted
    /// beside whole ones is a drop the market did not have, and it would draw as the strongest claim
    /// this column can make — liquidity vanishing — out of an absence of data. Rule 8 again.
    /// </summary>
    public IReadOnlyList<double?> Depth25 => Hours
        .Select(h => h is { DepthBid25BpsAvg: { } bid, DepthAsk25BpsAvg: { } ask } ? bid + ask : (double?)null)
        .ToList();
}

/// <summary>
/// Everything one venue's row on the comparison needs, gathered so the view reads a row rather than
/// three parallel collections indexed by hand.
/// </summary>
/// <param name="Ages">The three ages, computed once against the time of the request.</param>
/// <param name="Candles">The price history: the candle panel, and the line under bid, ask and last.</param>
/// <param name="Metrics">
/// The other four of rule 11's seven series — spread, funding, open interest and depth 25bps — on
/// the same hourly windows as <paramref name="Candles"/>, so all seven lines on this row describe
/// the same twenty-five hours.
/// </param>
public sealed record VenueRowModel(
    PairVenueRow Row,
    FreshnessWindows Windows,
    CallAges Ages,
    CandleSeries Candles,
    MetricHourSeries Metrics);

/// <summary>
/// The age of each of the three calls behind one row, in seconds, against the time of the request.
///
/// Three, because they are three calls with three clocks: a row can hold a two-second price and a
/// six-minute depth sweep and it has to be able to say so. Null is not zero — it is a call that has
/// never landed for this instrument, and the figures it would have written are dashes.
/// </summary>
public sealed record CallAges(double? PriceSeconds, double? OpenInterestSeconds, double? DepthSeconds);

/// <summary>The pair page, assembled: rows, verdicts, bar scales, and the instants the header
/// prints.</summary>
/// <param name="CollectedFrom">
/// The OLDEST observation on the page, across all three calls on every row. Null when nothing on
/// the page has ever been observed, which is a dash in the header and not a zero.
/// </param>
/// <param name="CollectedTo">
/// The freshest, on the same terms.
///
/// <b>Two instants and not one.</b> A single "collected" stamp was the maximum received_at, which is
/// the healthiest row on the page standing in for the whole of it: with one venue frozen three days
/// ago and the rest live, the header printed the current instant over a table a third of which had
/// not been observed since. It also read only <c>received_at</c> and ignored <c>depth_at</c> and
/// <c>open_interest_at</c> entirely — on the one surface whose thesis is that those are three
/// separate clocks. A span answers the question a single stamp invites ("how old is this page?")
/// without picking a winner: everything here was observed between these two instants, and the
/// per-cell ages say which figure sits where inside them.
/// </param>
/// <param name="RenderedAt">
/// When this HTML was built. Both marks are printed, per blueprint §5, because they are different
/// facts and a page that showed only one would let the reader mistake either for the other.
/// </param>
public sealed record PairPageModel(
    string BaseFamily,
    string QuoteFamily,
    IReadOnlyList<VenueRowModel> Rows,
    VerdictTable Verdicts,
    ColumnScales Scales,
    DateTime? CollectedFrom,
    DateTime? CollectedTo,
    DateTimeOffset RenderedAt)
{
    /// <summary>Every instant behind a figure on this page — three calls per row, absent where a
    /// call has never landed. The header's span is the two ends of this.</summary>
    public static IEnumerable<DateTime> Observations(IEnumerable<VenueRowModel> rows) =>
        rows.SelectMany(r => new[] { r.Row.ReceivedAt, r.Row.OpenInterestAt, r.Row.DepthAt })
            .Where(t => t is not null)
            .Select(t => t!.Value);

    /// <summary>
    /// The two ends of that, which is what the header prints.
    ///
    /// Both ends together, in one place, so the header cannot go back to reporting one of them as
    /// though it described the page: the maximum alone is the healthiest row standing in for the
    /// whole table. Both null when nothing here has ever been observed — a dash, never a zero and
    /// never the render instant.
    /// </summary>
    public static (DateTime? From, DateTime? To) CollectedSpan(IReadOnlyList<VenueRowModel> rows)
    {
        var observations = Observations(rows).ToList();
        return observations.Count == 0 ? (null, null) : (observations.Min(), observations.Max());
    }
}

/// <summary>The pair list page.</summary>
/// <param name="Matching">
/// How many pairs the filter matched, which is not <c>Pairs.Count</c> whenever the board is full.
/// It is carried to the view so the page can say what it is not showing: a board that quietly
/// rendered its first two hundred cards and left the reader to assume that was all of them would be
/// the same failure as a zero standing in for a dash.
/// </param>
/// <param name="Limit">
/// The ceiling that produced this list (<see cref="Data.ArenaStore.MaxPairs"/>). Printed rather than
/// implied, because "200 of 4,812" only means something to a reader who can see that the 200 is a
/// rule of this page and not a fact about the market.
/// </param>
public sealed record PairListModel(
    IReadOnlyList<PairListItem> Pairs,
    int Matching,
    int Limit,
    string Search,
    DateTimeOffset RenderedAt)
{
    /// <summary>Whether the board is showing fewer pairs than matched. One place, so the statement
    /// line and the note below the cards cannot disagree about it.</summary>
    public bool Truncated => Matching > Pairs.Count;
}

using CryptoSmithX.WebApp.Studio.Models;
using CryptoSmithX.Database;

namespace CryptoSmithX.WebApp.Studio.Data;

/// <summary>
/// The one place that turns a base-asset address into a <see cref="PairPageModel"/>.
///
/// It lives here rather than on a controller because there are now TWO pages drawing this reading —
/// /studio/ARB and /studio/v2/ARB — and a second loader would let them disagree about the market at
/// the same instant. On a site whose whole subject is "what did each venue say", two answers to that
/// question from one process is the one contradiction that cannot be explained away. Same query,
/// same cache key, same verdicts: two presentations of one reading.
/// </summary>
public static class PairPageLoader
{
    public static async Task<PairPageModel?> LoadAsync(
        Db db, StudioCache cache, TimeProvider clock, string baseFamily, CancellationToken ct)
    {
        var data = await cache.GetAsync<PairData?>(
            "asset:" + baseFamily,
            async token =>
            {
                await using var conn = await db.OpenAsync(token);
                var comparison = await StudioStore.GetAssetAsync(conn, baseFamily, token);
                if (comparison is null)
                {
                    return null;
                }

                var ids = comparison.Venues.Select(v => v.Row.InstrumentId).ToList();

                // Two series queries on one connection, both anchored to the same instant and both
                // resolved onto the same window list (CandleStore.Windows). Seven lines are drawn
                // per row from these two results and a reader compares them across the row, so they
                // have to be describing the same twenty-five hours — two anchors would put the
                // price line and the open-interest line an hour apart with nothing on the page to
                // say so.
                var at = clock.GetUtcNow();
                var candles = await CandleStore.ReadAsync(conn, ids, at, token);
                var metrics = await MetricHourStore.ReadAsync(conn, ids, at, token);

                // Пять новых чтений второй страницы. На той же связи и в том же кешируемом
                // значении, что и остальные: страница, задающая базе восемь вопросов, чтобы
                // нарисовать один экран, расходится сама с собой между вторым и седьмым.
                var segments = comparison.Venues.Select(v => v.Row.SegmentCode).Distinct(StringComparer.Ordinal).ToArray();
                var books = await V2Store.BooksAsync(conn, ids, token);
                var tape = await V2Store.TapeAsync(conn, ids, 18, token);
                var coverage = await V2Store.CoverageAsync(conn, ids, token);
                var gaps = await V2Store.GapsAsync(conn, ids, segments, token);
                var stress = await V2Store.StressAsync(conn, ids, token);
                var liquidations = await V2Store.LiquidationsAsync(conn, ids, at, token);

                return new PairData(
                    comparison, candles, metrics, books, tape, coverage, gaps, stress, liquidations);
            },
            ct);

        if (data is null)
        {
            return null;
        }

        // Every age on the page is a subtraction against THIS instant — the time of the request —
        // and never against the moment the cache filled. The payload above holds only absolute
        // instants, which is what makes a second-old answer still able to report a truthful age
        // (blueprint §5). Doing the subtraction anywhere else would undo that.
        //
        // The live stream calls this method again per push for exactly the same reason: it must
        // never re-send a fragment it rendered a minute ago, because the ages baked into that
        // fragment were true a minute ago.
        var now = clock.GetUtcNow();
        var comparison = data.Comparison;

        var rows = comparison.Venues
            .Select(v => new VenueRowModel(
                v.Row,
                v.Windows,
                new CallAges(
                    Freshness.AgeSeconds(v.Row.ReceivedAt, now),
                    Freshness.AgeSeconds(v.Row.OpenInterestAt, now),
                    Freshness.AgeSeconds(v.Row.DepthAt, now)),
                data.Candles.TryGetValue(v.Row.InstrumentId, out var c) ? c : CandleSeries.Empty,
                data.Metrics.TryGetValue(v.Row.InstrumentId, out var m) ? m : MetricHourSeries.Empty)
            {
                Liquidations = data.Liquidations.TryGetValue(v.Row.InstrumentId, out var l) ? l : [],
            })
            .ToList();

        // The span the observations on this page actually cover, across all three calls on every
        // row. Both ends, and never the maximum alone: the freshest row on the page is not a
        // statement about the page, and a header printing it as one tells a reader that a table
        // holding a three-day-old venue is seconds old. Both are null — a dash in the header, not a
        // zero — when nothing here has ever been observed, which is a real state: discovery lists an
        // instrument the moment the venue announces it, and the first snapshot arrives later.
        var collected = PairPageModel.CollectedSpan(rows);

        // Computed HERE and not where the comparison was loaded, because a rank is now withheld
        // from a figure whose call has gone degraded and that is a judgement against `now`. Putting
        // it back in the cached payload would freeze the freshness half of it at the instant the
        // cache filled (blueprint §5, and the note on PairComparison).
        var verdicts = Verdicts.Compute(rows);

        return new PairPageModel(
            comparison.BaseFamily,
            rows,
            verdicts,
            ColumnScales.Compute(rows),
            collected.From,
            collected.To,
            now)
        {
            Books = data.Books,
            Tape = data.Tape,
            Coverage = data.Coverage,
            Gaps = data.Gaps,
            Stress = data.Stress,
        };
    }

    /// <summary>
    /// The three reads behind one page, cached as one value because they are fetched on one
    /// connection for one request and splitting them would double the round trips to halve nothing.
    ///
    /// It carries no "now" — deliberately, and that is the property the whole freshness model rests
    /// on. See <see cref="StudioCache"/>.
    /// </summary>
    private sealed record PairData(
        AssetComparison Comparison,
        IReadOnlyDictionary<int, CandleSeries> Candles,
        IReadOnlyDictionary<int, MetricHourSeries> Metrics,
        IReadOnlyDictionary<int, BookFrame> Books,
        IReadOnlyList<TapeRow> Tape,
        IReadOnlyList<CoverageCell> Coverage,
        IReadOnlyList<GapRow> Gaps,
        IReadOnlyDictionary<int, StressRow> Stress,
        IReadOnlyDictionary<int, IReadOnlyList<double?>> Liquidations);
}

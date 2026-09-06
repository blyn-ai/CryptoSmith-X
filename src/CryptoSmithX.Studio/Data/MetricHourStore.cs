using System.Data.Common;
using CryptoSmithX.Studio.Models;
using Dapper;

namespace CryptoSmithX.Studio.Data;

/// <summary>
/// The four hourly series rule 11 asks for that the price history cannot supply: spread, funding,
/// open interest and depth 25bps.
///
/// <b>Why this file exists at all.</b> Rule 11 gives seven columns on a perpetual row a line. Three
/// of them — bid, ask and last — sit on one price series, read from <c>market_candle</c> by
/// <see cref="CandleStore"/>. The other four have no price column behind them; their hourly grain
/// is <c>market_metric_hour</c> (0006), and nothing else in the schema holds it. Until 0026 that
/// table was not granted to <c>studio_reader</c>, so those four columns rendered a figure, an age,
/// and eleven pixels of nothing where the line belongs — a reserved slot the page could not fill,
/// and one indistinguishable from the legitimately empty slot on mark and index, which rule 11
/// gives no second dimension at all. Same pixels, opposite meanings. 0026 carries the argument.
///
/// <b>Why the aggregate and not the snapshots.</b> Rolling the series up here from
/// <c>market_snapshot</c> would be <c>RollupJob</c> re-implemented in a view layer, against a table
/// that already holds the answer — and against the one table 0025 withholds precisely so that query
/// cannot be written on this surface. Interpolating a line between the two snapshots the page can
/// see would be worse still: history invented on the one surface whose entire argument is that it
/// invents nothing.
/// </summary>
public static class MetricHourStore
{
    /// <summary>
    /// One row per instrument-hour, over the window list the candles are drawn on.
    ///
    /// The floor is an absolute instant computed by the caller and passed in, never <c>now()</c> in
    /// SQL: every instant this application prints is computed against the time of the REQUEST
    /// (blueprint §5), and a query that read the database's clock would put a second clock on a page
    /// whose subject is that there is only ever one.
    ///
    /// <c>snapshot_count</c> is read and deliberately not drawn. It is the honest caveat on the row
    /// — how many snapshots the hour was averaged from — and the page has no room for a per-point
    /// confidence mark under an eleven-pixel line. It is here because the caller uses it to refuse
    /// an hour that rests on nothing; see <see cref="MinSnapshots"/>.
    /// </summary>
    public const string Sql =
        """
        select m.exchange_instrument_id as "InstrumentId",
               m.hour_time              as "HourTime",
               m.spread_bps_avg         as "SpreadBpsAvg",
               m.funding_rate_last      as "FundingRateLast",
               m.open_interest_last     as "OpenInterestLast",
               m.depth_bid_25bps_avg    as "DepthBid25BpsAvg",
               m.depth_ask_25bps_avg    as "DepthAsk25BpsAvg",
               m.snapshot_count         as "SnapshotCount"
          from market_metric_hour m
         where m.exchange_instrument_id = any(@instrumentIds)
           and m.hour_time >= @from
           and m.hour_time <= @anchor
         order by m.exchange_instrument_id, m.hour_time
        """;

    /// <summary>
    /// The fewest snapshots an hour may rest on and still be drawn.
    ///
    /// One. Not a threshold dressed up as quality control — an hour built from a single snapshot is
    /// still a measurement that was taken, and dropping it would be the page deciding a real
    /// observation was not good enough to show, which is the judgement it refuses to make anywhere
    /// else. The constant exists so that a row with a <c>snapshot_count</c> of zero — which the
    /// schema's <c>not null</c> permits and the aggregate should never write — is a gap rather than
    /// a point drawn from nothing.
    /// </summary>
    public const short MinSnapshots = 1;

    /// <summary>
    /// One series per instrument asked for, all of them on the same windows as the candles.
    ///
    /// Gaps stay gaps, for the reason <see cref="CandleStore.ReadAsync"/> gives at length: a venue
    /// that went dark for six hours must not have its remaining points slide together and draw as an
    /// unbroken line. An instrument with no rows still gets a series — an empty one — because "this
    /// venue has no hourly history" is a fact about the venue and not a reason to omit it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, MetricHourSeries>> ReadAsync(
        DbConnection conn, IReadOnlyList<int> instrumentIds, DateTimeOffset now, CancellationToken ct)
    {
        if (instrumentIds.Count == 0)
        {
            return new Dictionary<int, MetricHourSeries>();
        }

        var windows = CandleStore.Windows(now);

        var rows = (await conn.QueryAsync<MetricHourRow>(new CommandDefinition(
            Sql,
            new
            {
                instrumentIds = instrumentIds.ToArray(),
                from = windows[0],
                anchor = windows[^1]
            },
            cancellationToken: ct))).ToList();

        var byInstrument = rows
            .Where(r => r.SnapshotCount >= MinSnapshots)
            .GroupBy(r => r.InstrumentId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.HourTime));

        return instrumentIds.Distinct().ToDictionary(
            id => id,
            id =>
            {
                byInstrument.TryGetValue(id, out var byTime);
                var hours = windows
                    .Select(w => byTime is not null && byTime.TryGetValue(w, out var r) ? r : null)
                    .ToList();
                return new MetricHourSeries(windows, hours);
            });
    }
}

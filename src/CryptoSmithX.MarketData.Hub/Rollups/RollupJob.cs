using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Rollups;

/// <summary>
/// Builds every derived timeframe from the 1-minute bars. One instance for the whole service: the
/// work is per instrument and the source table is shared.
///
/// The unit of work is a window that has been *touched* — any window holding a 1m bar written or
/// rewritten since the last pass. That is what lets a bar arriving late repair its parents instead
/// of leaving them wrong forever, and it keeps a pass proportional to what actually changed. The
/// aggregation itself runs as one <c>insert ... select ... group by ... on conflict</c> statement
/// per timeframe — no 1-minute bar ever crosses the wire into this process. At 1,500 instruments a
/// touched daily window alone is ~2.1M minute rows; pulling that into memory to <c>GroupBy</c> in
/// C# is what took the pass to 142s and knocked snapshots offline. <see cref="Rollup"/> is kept as
/// the arithmetic's specification — unit-tested in isolation — even though this class no longer
/// calls it; the SQL below is required to mean the same thing.
/// </summary>
public sealed class RollupJob
{
    /// <summary>
    /// How far back to look for touched bars. Wider than the interval so a slow pass, or a
    /// restart, does not leave a window unrepaired.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(10);

    /// <summary>First-ever pass (no persisted watermark yet): bounded to a day rather than the
    /// start of the table, so a fresh deploy never re-touches the whole history. Deeper history,
    /// when it is actually wanted, is a deliberate backfill — not something a cold start does
    /// silently.</summary>
    private static readonly TimeSpan ColdStartWindow = TimeSpan.FromDays(1);

    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RollupJob> _logger;
    private readonly string _serviceExchange;
    private readonly string _collector;

    /// <summary>
    /// <paramref name="serviceExchange"/> and <paramref name="collector"/> identify this job's own
    /// row in <c>collector_status</c> — the same row <see cref="Ingestion.CollectorLoop"/> already
    /// upserts <c>last_success_at</c> into after every run that did not throw. Reading it back here,
    /// instead of keeping a private field, is the watermark: it survives a restart for free, and it
    /// only ever advances on success (a failed or cancelled pass leaves <c>last_success_at</c>
    /// untouched, so the next pass retries the same window instead of either losing it or rewinding
    /// to the epoch).
    /// </summary>
    public RollupJob(
        DbSettings settings, Db db, TimeProvider clock, ILogger<RollupJob> logger,
        string serviceExchange, string collector)
    {
        _settings = settings;
        _db = db;
        _clock = clock;
        _logger = logger;
        _serviceExchange = serviceExchange;
        _collector = collector;
    }

    /// <summary>The start of the lookback window for this pass. Pure so the cold-start bound and
    /// the slack-behind-the-watermark rule are each asserted directly.</summary>
    internal static DateTimeOffset SinceFor(DateTimeOffset? lastSuccessAt, DateTimeOffset startedAt) =>
        lastSuccessAt is { } last ? last - Slack : startedAt - ColdStartWindow;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        var derivedTimeframes = (await _settings.CurrentAsync(ct)).CollectionSettingIntList("rollup", "derived_timeframes");

        await using var conn = await _db.OpenAsync(ct);
        await Partitions.EnsureAsync(conn, startedAt, ct);

        // Npgsql hands a scalar timestamptz back as DateTime, not DateTimeOffset — same as every
        // row-mapped timestamp in this file (see Utc below); asking Dapper for DateTimeOffset
        // directly throws on the Convert.ChangeType it falls back to for a scalar read.
        var lastSuccessAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "select last_success_at from collector_status where exchange_code = @exchange and collector = @collector",
            new { exchange = _serviceExchange, collector = _collector },
            cancellationToken: ct));
        var since = SinceFor(lastSuccessAt is { } t ? Utc(t) : null, startedAt);

        var written = 0;
        foreach (var tf in derivedTimeframes.Where(t => t > 1).Distinct().OrderBy(t => t))
        {
            var seconds = tf * 60L;

            // Every touched window, aggregated where it lives. "touched" only widens the CTE's own
            // distinct scan (cheap: 1m rows since `since`); the join and group by then aggregate the
            // window's full set of minutes without any of them ever leaving Postgres. open/close are
            // the array's first/last element by time, not arrival order — the same rule Rollup.Build
            // encodes, just expressed as `order by open_time` instead of a LINQ OrderBy.
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                """
                with touched as (
                    select distinct
                           c.exchange_instrument_id,
                           to_timestamp(floor(extract(epoch from c.open_time) / @seconds) * @seconds) as window_start
                      from market_candle c
                     where c.timeframe = 1
                       and c.updated_at >= @since
                ),
                windows as (
                    select t.exchange_instrument_id,
                           t.window_start,
                           count(*)                                              as bar_count,
                           (array_agg(c.open  order by c.open_time asc))[1]      as open,
                           max(c.high)                                          as high,
                           min(c.low)                                           as low,
                           (array_agg(c.close order by c.open_time desc))[1]     as close,
                           sum(c.volume)                                        as volume,
                           case when bool_and(c.trade_count is not null)
                                then sum(c.trade_count) else null end            as trade_count
                      from touched t
                      join market_candle c
                        on  c.exchange_instrument_id = t.exchange_instrument_id
                        and c.timeframe = 1
                        and c.open_time >= t.window_start
                        and c.open_time <  t.window_start + make_interval(secs => @seconds)
                     where t.window_start + make_interval(secs => @seconds) <= now()
                     group by t.exchange_instrument_id, t.window_start
                )
                insert into market_candle (
                    exchange_instrument_id, timeframe, open_time,
                    open, high, low, close, volume, trade_count, bar_count, updated_at)
                select exchange_instrument_id, @tf, window_start,
                       open, high, low, close, volume, trade_count, bar_count, now()
                  from windows
                on conflict (exchange_instrument_id, timeframe, open_time) do update set
                    open        = excluded.open,
                    high        = excluded.high,
                    low         = excluded.low,
                    close       = excluded.close,
                    volume      = excluded.volume,
                    trade_count = excluded.trade_count,
                    bar_count   = excluded.bar_count,
                    updated_at  = excluded.updated_at
                """,
                new { seconds = (double)seconds, since, tf = (short)tf },
                cancellationToken: ct));
            written += affected;
        }

        // Last step: the hourly microstructure slice. Open interest, spread and depth die with the
        // 90-day snapshot rotation, so every closed hour touched since the watermark is folded into
        // market_metric_hour, which is not rotated. Recomputed whole on every touch, the same way a
        // derived candle is — a late snapshot repairs its hour instead of leaving it wrong. Snapshot
        // volume is the same order of magnitude as 1m candles, so this now benefits from the same
        // bounded/persisted `since` above; it stays in C# because a pass only ever reads the last
        // `since`-to-now slice (tens of thousands of rows at 1,500 instruments), not whole windows.
        var metricRows = await conn.QueryAsync<MetricRow>(new CommandDefinition(
            """
            select s.exchange_instrument_id          as InstrumentId,
                   date_trunc('hour', s.received_at) as HourTime,
                   s.received_at                     as ReceivedAt,
                   s.bid_price                        as BidPrice,
                   s.ask_price                        as AskPrice,
                   s.open_interest                    as OpenInterest,
                   s.funding_rate                     as FundingRate,
                   s.depth_bid_25bps                  as DepthBid25,
                   s.depth_ask_25bps                  as DepthAsk25
              from market_snapshot s
             where s.received_at >= @since
               and s.received_at < date_trunc('hour', now())
            """,
            new { since },
            cancellationToken: ct));

        var metrics = 0;
        foreach (var hour in metricRows.GroupBy(r => (r.InstrumentId, r.HourTime)))
        {
            var bar = MetricHour.Aggregate(
                Utc(hour.Key.HourTime),
                hour.Select(r => new MetricSnapshot(
                    Utc(r.ReceivedAt), r.BidPrice, r.AskPrice, r.OpenInterest, r.FundingRate,
                    r.DepthBid25, r.DepthAsk25)).ToList());

            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into market_metric_hour (
                    exchange_instrument_id, hour_time, open_interest_last, funding_rate_last,
                    spread_bps_avg, depth_bid_25bps_avg, depth_ask_25bps_avg, snapshot_count, updated_at)
                values (@InstrumentId, @HourTime, @OpenInterestLast, @FundingRateLast,
                        @SpreadBpsAvg, @DepthBid25BpsAvg, @DepthAsk25BpsAvg, @SnapshotCount, now())
                on conflict (exchange_instrument_id, hour_time) do update set
                    open_interest_last  = excluded.open_interest_last,
                    funding_rate_last   = excluded.funding_rate_last,
                    spread_bps_avg      = excluded.spread_bps_avg,
                    depth_bid_25bps_avg = excluded.depth_bid_25bps_avg,
                    depth_ask_25bps_avg = excluded.depth_ask_25bps_avg,
                    snapshot_count      = excluded.snapshot_count,
                    updated_at          = excluded.updated_at
                """,
                new
                {
                    InstrumentId = hour.Key.InstrumentId,
                    bar.HourTime,
                    bar.OpenInterestLast,
                    bar.FundingRateLast,
                    bar.SpreadBpsAvg,
                    bar.DepthBid25BpsAvg,
                    bar.DepthAsk25BpsAvg,
                    bar.SnapshotCount,
                },
                cancellationToken: ct));
            metrics++;
        }

        if (written > 0 || metrics > 0)
        {
            _logger.LogDebug("Rollup wrote {Rows} derived bars and {Metrics} metric hours", written, metrics);
        }

        return written;
    }

    // Npgsql hands back timestamptz as DateTime with Kind=Utc; converted at the boundary below.
    private static DateTimeOffset Utc(DateTime t) =>
        new(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeSpan.Zero);

    private sealed record MetricRow(
        int InstrumentId,
        DateTime HourTime,
        DateTime ReceivedAt,
        double BidPrice,
        double AskPrice,
        double OpenInterest,
        double FundingRate,
        double? DepthBid25,
        double? DepthAsk25);
}

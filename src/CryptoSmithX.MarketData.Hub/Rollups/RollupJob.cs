using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Rollups;

/// <summary>
/// Builds every derived timeframe from the 1-minute bars. One instance for the whole service: the
/// work is per instrument and the source table is shared.
///
/// The unit of work is a window that has been *touched* — any window holding a 1m bar written or
/// rewritten since the last pass. That is what lets a bar arriving late repair its parents instead
/// of leaving them wrong forever, and it keeps a pass proportional to what actually changed.
/// The arithmetic itself lives in <see cref="Rollup"/>.
/// </summary>
public sealed class RollupJob
{
    /// <summary>
    /// How far back to look for touched bars. Wider than the interval so a slow pass, or a
    /// restart, does not leave a window unrepaired.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(10);

    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RollupJob> _logger;

    private DateTimeOffset _watermark = DateTimeOffset.UnixEpoch;

    public RollupJob(DbSettings settings, Db db, TimeProvider clock, ILogger<RollupJob> logger)
    {
        _settings = settings;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        var since = _watermark == DateTimeOffset.UnixEpoch ? _watermark : _watermark - Slack;
        var derivedTimeframes = (await _settings.CurrentAsync(ct)).CollectionSettingIntList("rollup", "derived_timeframes");

        await using var conn = await _db.OpenAsync(ct);
        await Partitions.EnsureAsync(conn, startedAt, ct);

        var written = 0;
        foreach (var tf in derivedTimeframes.Where(t => t > 1).Distinct().OrderBy(t => t))
        {
            var seconds = tf * 60L;

            // Every 1m bar of every window that has been touched — the whole window, not only the
            // minutes that changed, because an aggregate needs all of its parts.
            var rows = await conn.QueryAsync<MinuteRow>(new CommandDefinition(
                """
                with touched as (
                    select distinct
                           c.exchange_instrument_id,
                           to_timestamp(floor(extract(epoch from c.open_time) / @seconds) * @seconds) as window_start
                      from market_candle c
                     where c.timeframe = 1
                       and c.updated_at >= @since
                )
                select c.exchange_instrument_id as InstrumentId,
                       t.window_start           as WindowStart,
                       c.open_time              as OpenTime,
                       c.open                   as Open,
                       c.high                   as High,
                       c.low                    as Low,
                       c.close                  as Close,
                       c.volume                 as Volume,
                       c.trade_count            as TradeCount
                  from touched t
                  join market_candle c
                    on  c.exchange_instrument_id = t.exchange_instrument_id
                    and c.timeframe = 1
                    and c.open_time >= t.window_start
                    and c.open_time <  t.window_start + make_interval(secs => @seconds)
                 where t.window_start + make_interval(secs => @seconds) <= now()
                """,
                new { seconds = (double)seconds, since },
                cancellationToken: ct));

            var byWindow = rows.GroupBy(r => (r.InstrumentId, r.WindowStart));
            foreach (var window in byWindow)
            {
                var bar = Rollup.Aggregate(
                    Utc(window.Key.WindowStart),
                    window.Select(r => new MinuteBar(
                        Utc(r.OpenTime), r.Open, r.High, r.Low, r.Close, r.Volume, r.TradeCount)).ToList());

                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into market_candle (
                        exchange_instrument_id, timeframe, open_time,
                        open, high, low, close, volume, trade_count, bar_count, updated_at)
                    values (@InstrumentId, @Timeframe, @OpenTime,
                            @Open, @High, @Low, @Close, @Volume, @TradeCount, @BarCount, now())
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
                    new
                    {
                        InstrumentId = window.Key.InstrumentId,
                        Timeframe = (short)tf,
                        bar.OpenTime,
                        bar.Open,
                        bar.High,
                        bar.Low,
                        bar.Close,
                        bar.Volume,
                        bar.TradeCount,
                        bar.BarCount,
                    },
                    cancellationToken: ct));
                written++;
            }
        }

        // Last step: the hourly microstructure slice. Open interest, spread and depth die with the
        // 90-day snapshot rotation, so every closed hour touched since the watermark is folded into
        // market_metric_hour, which is not rotated. Recomputed whole on every touch, the same way a
        // derived candle is — a late snapshot repairs its hour instead of leaving it wrong.
        var metricRows = await conn.QueryAsync<MetricRow>(new CommandDefinition(
            """
            select s.exchange_instrument_id          as InstrumentId,
                   date_trunc('hour', s.received_at) as HourTime,
                   s.received_at                     as ReceivedAt,
                   s.bid_price                       as BidPrice,
                   s.ask_price                       as AskPrice,
                   s.open_interest                   as OpenInterest,
                   s.funding_rate                    as FundingRate,
                   s.depth_bid_25bps                 as DepthBid25,
                   s.depth_ask_25bps                 as DepthAsk25
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

        _watermark = startedAt;
        if (written > 0 || metrics > 0)
        {
            _logger.LogDebug("Rollup wrote {Rows} derived bars and {Metrics} metric hours", written, metrics);
        }

        return written;
    }

    // Npgsql hands back timestamptz as DateTime with Kind=Utc; converted at the boundary below.
    private static DateTimeOffset Utc(DateTime t) =>
        new(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeSpan.Zero);

    private sealed record MinuteRow(
        int InstrumentId,
        DateTime WindowStart,
        DateTime OpenTime,
        double Open,
        double High,
        double Low,
        double Close,
        double Volume,
        int? TradeCount);

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

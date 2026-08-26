using CryptoSmithX.MarketData.Options;
using CryptoSmithX.MarketData.Storage;
using Dapper;

namespace CryptoSmithX.MarketData.Rollups;

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

    private readonly MarketDataOptions _options;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<RollupJob> _logger;

    private DateTimeOffset _watermark = DateTimeOffset.UnixEpoch;

    public RollupJob(MarketDataOptions options, Db db, TimeProvider clock, ILogger<RollupJob> logger)
    {
        _options = options;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        var since = _watermark == DateTimeOffset.UnixEpoch ? _watermark : _watermark - Slack;

        await using var conn = await _db.OpenAsync(ct);
        await Partitions.EnsureAsync(conn, startedAt, ct);

        var written = 0;
        foreach (var tf in _options.DerivedTimeframes.Where(t => t > 1).Distinct().OrderBy(t => t))
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

        _watermark = startedAt;
        if (written > 0)
        {
            _logger.LogDebug("Rollup wrote {Rows} derived bars", written);
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
}

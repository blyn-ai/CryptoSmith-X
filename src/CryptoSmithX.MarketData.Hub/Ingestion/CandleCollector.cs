using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Pulls closed 1-minute bars per instrument, starting after the newest bar already stored and
/// bounded so a first run cannot ask a venue for a year of history.
///
/// One REST call per instrument, same as <see cref="DepthCollector"/> and
/// <see cref="FundingCollector"/> — on WEEX that is on the order of a thousand calls a pass. Every
/// one of them goes through the venue's shared <see cref="VenueGate"/> (0021): this loop used to
/// issue them unpaced while depth alone respected the ceiling, which meant the venue-wide budget
/// the gate exists to enforce was routinely blown by the other two-thirds of the traffic.
/// </summary>
public sealed class CandleCollector
{
    // Back-fill targets: not delisted, and not turned off by an operator.
    internal const string TargetInstrumentsSql =
        """
        select i.id,
               i.exchange_symbol,
               (select max(c.open_time)
                  from market_candle c
                 where c.exchange_instrument_id = i.id and c.timeframe = 1) as latest
          from exchange_instrument i
         where i.segment_code = @code
           -- Halted as well as delisted. A halted contract has no trades to return, and WEEX's
           -- dead tail 400s on /candles rather than answering empty; they used to be kept out by
           -- being dropped from discovery entirely, which cost us the lifecycle fact. Now the fact
           -- is recorded and the exclusion happens here, where it belongs.
           and i.status not in ('delisted', 'halted')
           and i.collect = true
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly VenueGate _gate;

    public CandleCollector(IExchangeMarketData adapter, DbSettings settings, Db db, TimeProvider clock, VenueGate gate)
    {
        _adapter = adapter;
        _settings = settings;
        _db = db;
        _clock = clock;
        _gate = gate;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var floor = now - TimeSpan.FromHours((await _settings.CurrentAsync(ct)).DatasetSettingInt("candles", "backfill_hours"));

        await using var conn = await _db.OpenAsync(ct);
        await Partitions.EnsureAsync(conn, now, ct);

        var targets = (await conn.QueryAsync<(int Id, string Symbol, DateTimeOffset? Latest)>(new CommandDefinition(
            TargetInstrumentsSql,
            new { code = _adapter.SegmentCode },
            cancellationToken: ct))).ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        // One connection for the pass, as in DepthCollector, and every write — including the
        // per-symbol transaction below — behind one lane. The transaction is why the lane must wrap
        // the whole write and not just each statement: two workers interleaving BEGIN/COMMIT on one
        // Npgsql connection is not a slow pass, it is a broken one.
        using var writeLane = new SemaphoreSlim(1, 1);

        // One venue symbol whose endpoint is broken (WEEX serves 400 for a live market's candles)
        // must not starve every symbol after it. Per-symbol isolation, unchanged by the move to a
        // parallel walk: remember the failure, keep going; only an all-symbols failure fails the
        // pass — that is an outage, not a pothole.
        var result = await Sweep.RunAsync(
            targets,
            _gate.MaxConcurrentRequests,
            async (target, workCt) =>
            {
                var (id, symbol, latest) = target;

                // Re-ask for the newest stored minute as well: a venue that back-fills a late bar
                // then has a chance to correct it, and the rollup repairs the parents from there.
                var from = latest is null ? floor : latest.Value;
                if (from < floor)
                {
                    from = floor;
                }

                IReadOnlyList<Candle> candles;
                using (await _gate.AcquireAsync(workCt).ConfigureAwait(false))
                {
                    candles = await _adapter.GetCandles1mAsync(symbol, from, now, workCt);
                }

                if (candles.Count == 0)
                {
                    return 0;
                }

                await writeLane.WaitAsync(workCt).ConfigureAwait(false);
                try
                {
                    var stored = 0;
                    await using var tx = await conn.BeginTransactionAsync(workCt);
                    foreach (var c in candles)
                    {
                        await conn.ExecuteAsync(new CommandDefinition(
                            """
                            insert into market_candle (
                                exchange_instrument_id, timeframe, open_time,
                                open, high, low, close, volume, trade_count, bar_count, updated_at)
                            values (@Id, 1, @OpenTime, @Open, @High, @Low, @Close, @Volume, @TradeCount, 1, now())
                            on conflict (exchange_instrument_id, timeframe, open_time) do update set
                                open        = excluded.open,
                                high        = excluded.high,
                                low         = excluded.low,
                                close       = excluded.close,
                                volume      = excluded.volume,
                                trade_count = excluded.trade_count,
                                updated_at  = now()
                            """,
                            new { Id = id, c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.TradeCount },
                            tx, cancellationToken: workCt));
                        stored++;
                    }

                    await tx.CommitAsync(workCt);
                    return stored;
                }
                finally
                {
                    writeLane.Release();
                }
            },
            // A venue that pushed us away holds back every caller on this IP, not just this
            // collector: that is what the venue-wide gate is for.
            ex => VenuePenalty.Apply(_gate, ex),
            SweepFailure.Isolate,
            ct).ConfigureAwait(false);

        var written = result.Written;

        if (result.Failed > 0 && written == 0 && result.LastError is not null)
        {
            throw new InvalidOperationException($"every symbol failed; last: {result.LastError.Message}", result.LastError);
        }

        return written;
    }
}

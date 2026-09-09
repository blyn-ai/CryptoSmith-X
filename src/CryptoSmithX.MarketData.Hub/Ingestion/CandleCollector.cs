using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

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

        // Bars are gathered here across every symbol and written ONCE after the sweep, not one
        // transaction with one INSERT per bar per symbol. Measured on test, schema 0038, warmed:
        // Kraken's 275 symbols at 2-3 bars each took 5.98 s serialised, Binance's 44 took 1.47 s —
        // both while the HTTP side of the same passes, 32 wide, finished in well under a second.
        // Same fix as SnapshotCollector (e590bc1): a ConcurrentBag rather than a lock, since workers
        // only ever append and never read each other's entries.
        var fetched = new System.Collections.Concurrent.ConcurrentBag<(int Id, Candle Candle, string Source)>();

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

                // No stored bar at all is the only signal that distinguishes a genuine catch-up
                // request (source='backfill') from the normal rolling pull (source='rest') — the
                // same distinction that already decides `from` below, just carried through to the
                // write instead of being thrown away once the range is picked.
                var source = latest is null ? "backfill" : "rest";

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

                foreach (var c in candles)
                {
                    fetched.Add((id, c, source));
                }

                return candles.Count;
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

        if (!fetched.IsEmpty)
        {
            // One transaction for the whole pass, not one per symbol: the per-symbol BEGIN/COMMIT
            // this replaces existed only to isolate one worker's writes from another interleaving on
            // the same connection, which a single statement no longer can — there is nothing left to
            // isolate from.
            await using var tx = await conn.BeginTransactionAsync(ct);
            await WriteCandlesBatchAsync(conn, tx, fetched, _clock, ct);
            await tx.CommitAsync(ct);
        }

        return written;
    }

    /// <summary>Upsert every bar fetched this pass in one statement. <c>bar_count</c> stays the
    /// literal 1 every minute bar has always been written with; <c>on conflict do update</c> is load-
    /// bearing, not incidental — a venue corrects a late bar by re-serving its open_time, and the
    /// re-ask for the newest stored minute in <see cref="RunAsync"/> exists specifically to catch
    /// that correction, so a plain insert would fail the whole batch on the very bar this is for.</summary>
    private static async Task WriteCandlesBatchAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyCollection<(int Id, Candle Candle, string Source)> fetched,
        TimeProvider clock, CancellationToken ct)
    {
        var ids = new int[fetched.Count];
        var openTime = new DateTimeOffset[fetched.Count];
        var open = new double[fetched.Count];
        var high = new double[fetched.Count];
        var low = new double[fetched.Count];
        var close = new double[fetched.Count];
        var volume = new double[fetched.Count];
        var tradeCount = new int?[fetched.Count];
        var receivedAt = new DateTimeOffset[fetched.Count];
        var source = new string[fetched.Count];
        var volumeQuote = new double?[fetched.Count];

        var receivedNow = clock.GetUtcNow();
        var i = 0;
        foreach (var (id, c, src) in fetched)
        {
            ids[i] = id;
            openTime[i] = c.OpenTime;
            open[i] = c.Open;
            high[i] = c.High;
            low[i] = c.Low;
            close[i] = c.Close;
            volume[i] = c.Volume;
            tradeCount[i] = c.TradeCount;
            receivedAt[i] = receivedNow;
            source[i] = src;
            volumeQuote[i] = c.VolumeQuote;
            i++;
        }

        // NpgsqlCommand rather than Dapper: Dapper expands any IEnumerable parameter into
        // @p1, @p2, …, which turns unnest(@ids) into unnest((@ids1,@ids2,…)) and fails.
        //
        // known_from is left NULL on every row this collector writes: per its column comment
        // (0035) NULL already means "known from the moment it was received," which is exactly
        // what both 'rest' and 'backfill' rows are — the column exists for a future writer that
        // can claim earlier knowledge (e.g. an imported vendor dataset), not for this one.
        await using var cmd = new NpgsqlCommand(
            """
            insert into market_candle (
                exchange_instrument_id, timeframe, open_time,
                open, high, low, close, volume, trade_count, bar_count, updated_at,
                received_at, source, volume_quote)
            select v.id, 1, v.open_time, v.open, v.high, v.low, v.close, v.volume, v.trade_count, 1, now(),
                   v.received_at, v.source, v.volume_quote
              from unnest(
                       @ids, @open_time, @open, @high, @low, @close, @volume, @trade_count, @received_at,
                       @source, @volume_quote)
                   as v(id, open_time, open, high, low, close, volume, trade_count, received_at, source,
                        volume_quote)
            on conflict (exchange_instrument_id, timeframe, open_time) do update set
                open          = excluded.open,
                high          = excluded.high,
                low           = excluded.low,
                close         = excluded.close,
                volume        = excluded.volume,
                trade_count   = excluded.trade_count,
                updated_at    = now(),
                received_at   = excluded.received_at,
                source        = excluded.source,
                volume_quote  = excluded.volume_quote
            """,
            conn, tx);

        cmd.Parameters.AddWithValue("ids", ids);
        cmd.Parameters.AddWithValue("open_time", openTime);
        cmd.Parameters.AddWithValue("open", open);
        cmd.Parameters.AddWithValue("high", high);
        cmd.Parameters.AddWithValue("low", low);
        cmd.Parameters.AddWithValue("close", close);
        cmd.Parameters.AddWithValue("volume", volume);
        cmd.Parameters.AddWithValue("trade_count", tradeCount);
        cmd.Parameters.AddWithValue("received_at", receivedAt);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("volume_quote", volumeQuote);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

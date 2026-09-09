using System.Text.Json;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Fills <c>liquidation_volume_history</c>, and the two venues that have liquidation data at all
/// have it in completely different forms.
///
/// KRAKEN publishes a real aggregate: the analytics <c>liquidation-volume</c> series, one number per
/// hourly bucket in contract units. Fetched per symbol and written with source='analytics', which is
/// what the schema's own comment describes as the normal case for this table.
///
/// BINANCE publishes no aggregate at all any more — only <c>!forceOrder@arr</c>, one event per
/// liquidation order. Those events are buffered by the socket, drained here, and summed onto the same
/// hourly grid, with source='ws' to say plainly that the bucket is our arithmetic over the venue's
/// events rather than the venue's own total. That value is new in 0041, and the distinction matters:
/// a bucket we assembled starts when we started listening and misses whatever arrived while the
/// socket was down, which a venue-computed total would not.
///
/// This is NOT the thing 0032's table comment warned against ("built from our own
/// trade.trade_type=liquidation rows"). Those would be a derivation of a derivation — the tape does
/// not mark liquidations on Binance at all. This sums the venue's own dedicated liquidation stream,
/// which is the same fact its analytics endpoint used to serve pre-aggregated.
/// </summary>
public sealed class LiquidationCollector
{
    internal const string TargetInstrumentsSql =
        """
        select exchange_symbol, id
          from exchange_instrument
         where segment_code = @code and collect = true and status <> 'delisted'
        """;

    /// <summary>The grid both paths use, matching Kraken's analytics bucket so the two venues'
    /// series are the same shape.</summary>
    private const int BucketSeconds = 3600;

    private readonly IExchangeMarketData _adapter;
    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly VenueGate _gate;

    public LiquidationCollector(
        IExchangeMarketData adapter, DbSettings settings, Db db, TimeProvider clock, VenueGate gate)
    {
        _adapter = adapter;
        _settings = settings;
        _db = db;
        _clock = clock;
        _gate = gate;
    }

    /// <summary>Returns the number of buckets written.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var floor = now - TimeSpan.FromHours(
            (await _settings.CurrentAsync(ct)).DatasetSettingInt("liquidations", "backfill_hours"));

        await using var conn = await _db.OpenAsync(ct);
        var targets = (await conn.QueryAsync<(string Symbol, int Id)>(new CommandDefinition(
            TargetInstrumentsSql, new { code = _adapter.SegmentCode }, cancellationToken: ct))).ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        var idBySymbol = targets.ToDictionary(t => t.Symbol, t => t.Id, StringComparer.Ordinal);
        var buckets = new Dictionary<(int Id, DateTimeOffset At), (double Volume, string Unit, string Source)>();

        // The venue's own aggregate, where there is one.
        var fetched = new System.Collections.Concurrent.ConcurrentBag<(int Id, LiquidationBucket Bucket)>();
        var result = await Sweep.RunAsync(
            targets,
            _gate.MaxConcurrentRequests,
            async (target, workCt) =>
            {
                var (symbol, id) = target;
                IReadOnlyList<LiquidationBucket> series;
                using (await _gate.AcquireAsync(workCt).ConfigureAwait(false))
                {
                    series = await _adapter.GetLiquidationVolumeAsync(symbol, floor, now, workCt);
                }

                foreach (var b in series)
                {
                    fetched.Add((id, b));
                }

                return series.Count;
            },
            ex => VenuePenalty.Apply(_gate, ex),
            SweepFailure.Isolate,
            ct).ConfigureAwait(false);

        if (result.Failed > 0 && result.Written == 0 && result.LastError is not null)
        {
            throw new InvalidOperationException($"every symbol failed; last: {result.LastError.Message}", result.LastError);
        }

        foreach (var (id, b) in fetched)
        {
            buckets[(id, b.BucketTime)] = (b.Volume, b.VolumeUnit, "analytics");
        }

        // The venues that only stream events: sum them onto the same grid. Quantity in instrument
        // units, matching what the venue states on the order — see the class remarks.
        foreach (var e in _adapter.DrainLiquidations())
        {
            if (!idBySymbol.TryGetValue(e.ExchangeSymbol, out var id))
            {
                continue;
            }

            var key = (id, FloorTo(e.EventTime, BucketSeconds));
            var existing = buckets.GetValueOrDefault(key, (Volume: 0d, Unit: "base", Source: "ws"));

            // An analytics bucket already covering this window is the venue's own total and wins:
            // it counts what the venue saw, not only what our socket was up for.
            if (existing.Source == "analytics")
            {
                continue;
            }

            buckets[key] = (existing.Volume + e.Qty, "base", "ws");
        }

        if (buckets.Count == 0)
        {
            return 0;
        }

        var receivedAt = _clock.GetUtcNow();
        var rows = buckets
            .Select(b => new LiquidationRow(
                b.Key.Id, BucketSeconds, b.Key.At, b.Value.Volume, b.Value.Unit, receivedAt, b.Value.Source))
            .ToList();

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            insert into liquidation_volume_history (
                exchange_instrument_id, interval_s, bucket_time, volume, volume_unit, received_at, source)
            select exchange_instrument_id, interval_s, bucket_time, volume, volume_unit, received_at, source
              from jsonb_to_recordset(@rows) as x(
                   exchange_instrument_id integer, interval_s integer, bucket_time timestamptz,
                   volume numeric, volume_unit text, received_at timestamptz, source text)
            on conflict (exchange_instrument_id, interval_s, bucket_time) do update set
                -- An analytics total replaces anything we summed ourselves; our own running sum for
                -- the open bucket only ever grows, so taking the larger of the two never loses
                -- events already counted when a pass lands mid-bucket.
                volume      = case when excluded.source = 'analytics' then excluded.volume
                                   else greatest(liquidation_volume_history.volume, excluded.volume) end,
                volume_unit = excluded.volume_unit,
                received_at = excluded.received_at,
                source      = excluded.source
            """,
            conn, tx))
        {
            var json = cmd.Parameters.Add("rows", NpgsqlTypes.NpgsqlDbType.Jsonb);
            json.Value = JsonSerializer.Serialize(rows);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await Coverage.WriteAsync(
            conn, tx, "liquidations", now, receivedAt,
            rows.ConvertAll(r => (r.exchange_instrument_id, r.bucket_time)), ct);

        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private static DateTimeOffset FloorTo(DateTimeOffset at, int seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / seconds * seconds);

    private sealed record LiquidationRow(
        int exchange_instrument_id,
        int interval_s,
        DateTimeOffset bucket_time,
        double volume,
        string volume_unit,
        DateTimeOffset received_at,
        string source);
}

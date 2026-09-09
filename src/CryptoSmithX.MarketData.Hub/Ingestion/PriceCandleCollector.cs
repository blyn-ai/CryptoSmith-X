using System.Text.Json;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Fills <c>market_price_candle</c> for one series — mark or index. Two dataset codes
/// (<c>candles_mark</c>, <c>candles_index</c>) share this class because the two series differ only
/// in which endpoint answers; everything downstream of the fetch is identical, and the series
/// travels on the row rather than in the table name.
///
/// Only Binance publishes either. The other three adapters return an empty list from
/// <see cref="IExchangeMarketData.GetPriceCandles1mAsync"/> and declare no capability, so the loop
/// is never started for them — an empty table for those segments is the venue's silence, not a
/// missing writer.
/// </summary>
public sealed class PriceCandleCollector
{
    internal const string TargetInstrumentsSql =
        """
        select i.exchange_symbol,
               i.id,
               (select max(c.open_time)
                  from market_price_candle c
                 where c.exchange_instrument_id = i.id and c.series = @series and c.timeframe = 1) as latest
          from exchange_instrument i
         where i.segment_code = @code
           and i.status not in ('delisted', 'halted')
           and i.collect = true
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly VenueGate _gate;
    private readonly string _series;

    /// <param name="series">'mark' or 'index' — also the suffix of this collector's dataset code.</param>
    public PriceCandleCollector(
        IExchangeMarketData adapter, DbSettings settings, Db db, TimeProvider clock, VenueGate gate, string series)
    {
        _adapter = adapter;
        _settings = settings;
        _db = db;
        _clock = clock;
        _gate = gate;
        _series = series;
    }

    /// <summary>Returns the number of bars written.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var floor = now - TimeSpan.FromHours(
            (await _settings.CurrentAsync(ct)).DatasetSettingInt($"candles_{_series}", "backfill_hours"));

        await using var conn = await _db.OpenAsync(ct);
        await Partitions.EnsureAsync(conn, now, ct);

        var targets = (await conn.QueryAsync<(string Symbol, int Id, DateTimeOffset? Latest)>(new CommandDefinition(
                TargetInstrumentsSql,
                new { code = _adapter.SegmentCode, series = _series },
                cancellationToken: ct)))
            .ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        var fetched = new System.Collections.Concurrent.ConcurrentBag<(int Id, PriceCandle Candle, string Source)>();

        var result = await Sweep.RunAsync(
            targets,
            _gate.MaxConcurrentRequests,
            async (target, workCt) =>
            {
                var (symbol, id, latest) = target;

                // Same rest/backfill distinction the traded candles use: nothing stored yet means
                // this pull reaches back to the floor rather than rolling forward.
                var source = latest is null ? "backfill" : "rest";
                var from = latest ?? floor;
                if (from < floor)
                {
                    from = floor;
                }

                IReadOnlyList<PriceCandle> bars;
                using (await _gate.AcquireAsync(workCt).ConfigureAwait(false))
                {
                    bars = await _adapter.GetPriceCandles1mAsync(symbol, _series, from, now, workCt);
                }

                foreach (var b in bars)
                {
                    fetched.Add((id, b, source));
                }

                return bars.Count;
            },
            ex => VenuePenalty.Apply(_gate, ex),
            SweepFailure.Isolate,
            ct).ConfigureAwait(false);

        if (result.Failed > 0 && result.Written == 0 && result.LastError is not null)
        {
            throw new InvalidOperationException($"every symbol failed; last: {result.LastError.Message}", result.LastError);
        }

        if (fetched.IsEmpty)
        {
            return 0;
        }

        var receivedAt = _clock.GetUtcNow();
        var rows = fetched
            .Select(f => new PriceRow(
                f.Id, f.Candle.Series, f.Candle.OpenTime,
                f.Candle.Open, f.Candle.High, f.Candle.Low, f.Candle.Close, receivedAt, f.Source))
            .ToList();

        await using var tx = await conn.BeginTransactionAsync(ct);
        await BulkJson.WriteAsync(
            conn, tx,
            """
            insert into market_price_candle (
                exchange_instrument_id, series, timeframe, open_time,
                open, high, low, close, received_at, source)
            select exchange_instrument_id, series, 1, open_time,
                   open, high, low, close, received_at, source
              from jsonb_to_recordset(@rows) as x(
                   exchange_instrument_id integer, series text, open_time timestamptz,
                   open numeric, high numeric, low numeric, close numeric,
                   received_at timestamptz, source text)
            -- A venue may correct a late bar, same as with traded candles.
            on conflict (exchange_instrument_id, series, timeframe, open_time) do update set
                open        = excluded.open,
                high        = excluded.high,
                low         = excluded.low,
                close       = excluded.close,
                received_at = excluded.received_at,
                source      = excluded.source
            """,
            rows, ct);

        await Coverage.WriteAsync(
            conn, tx, $"candles_{_series}", now, receivedAt,
            rows.ConvertAll(r => (r.exchange_instrument_id, r.open_time)), ct);

        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private sealed record PriceRow(
        int exchange_instrument_id,
        string series,
        DateTimeOffset open_time,
        double open,
        double high,
        double low,
        double close,
        DateTimeOffset received_at,
        string source);
}

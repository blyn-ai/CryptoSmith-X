using System.Text.Json;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Fills <c>open_interest_history</c>, from whichever of the two things a venue actually has.
///
/// TWO VENUES PUBLISH A SERIES. Binance's <c>/futures/data/openInterestHist</c> gives one point per
/// 5-minute bucket plus a quote notional; Kraken's analytics <c>open-interest</c> gives real OHLC
/// per hour. Both are the venue's own aggregate, so both are written with source='analytics' and a
/// coverage row describing the answer.
///
/// TWO VENUES PUBLISH NOTHING. WEEX 404s on every openInterestHist spelling and Hyperliquid's info
/// API rejects the request outright — both were probed live before this was written. What they DO
/// publish is open interest right now, on the same ticker the snapshot collector already reads. So
/// for those two the series is built here: the current value, floored onto a fixed bucket grid, one
/// row per bucket, source='rest' — which is the schema's own word for "our own observation rather
/// than the venue's aggregate", and the reason oi_open/high/low stay NULL there (a single sample is
/// a close, not a bar).
///
/// Sampling like that is honest but it is NOT the venue's history: it starts when we start, and a
/// bucket we were down for is simply absent. That is why it is marked differently from the two
/// venues that can be asked about the past.
/// </summary>
public sealed class OpenInterestHistoryCollector
{
    // `latest` is what keeps a pass proportional to what is MISSING rather than to the configured
    // backfill window: without it every pass would re-ask each venue for the whole window and
    // re-upsert it, which on Kraken is 275 symbols x 720 hourly buckets every five minutes, forever.
    internal const string TargetInstrumentsSql =
        """
        select i.exchange_symbol,
               i.id,
               (select max(o.bucket_time)
                  from open_interest_history o
                 where o.exchange_instrument_id = i.id) as latest
          from exchange_instrument i
         where i.segment_code = @code and i.collect = true and i.status <> 'delisted'
        """;

    /// <summary>The grid the sampled venues are bucketed onto. Five minutes, matching the finest
    /// grain the one venue with a real series (Binance) publishes — so a reader comparing two venues
    /// is comparing the same shape of number.</summary>
    private const int SampledBucketSeconds = 300;

    private readonly IExchangeMarketData _adapter;
    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly VenueGate _gate;

    public OpenInterestHistoryCollector(
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
            (await _settings.CurrentAsync(ct)).DatasetSettingInt("open_interest", "backfill_hours"));

        await using var conn = await _db.OpenAsync(ct);
        var targets = (await conn.QueryAsync<(string Symbol, int Id, DateTimeOffset? Latest)>(new CommandDefinition(
            TargetInstrumentsSql, new { code = _adapter.SegmentCode }, cancellationToken: ct))).ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        var idBySymbol = targets.ToDictionary(t => t.Symbol, t => t.Id, StringComparer.Ordinal);
        var fetched = new System.Collections.Concurrent.ConcurrentBag<(int Id, OpenInterestBucket Bucket)>();

        var result = await Sweep.RunAsync(
            targets,
            _gate.MaxConcurrentRequests,
            async (target, workCt) =>
            {
                var (symbol, id, latest) = target;

                // From the newest stored bucket, bounded by the backfill floor. Re-asking for the
                // newest one is deliberate: an analytics bucket is only final once its window has
                // closed, so the last one stored is exactly the one that can still change.
                var from = latest ?? floor;
                if (from < floor)
                {
                    from = floor;
                }

                IReadOnlyList<OpenInterestBucket> buckets;
                using (await _gate.AcquireAsync(workCt).ConfigureAwait(false))
                {
                    buckets = await _adapter.GetOpenInterestHistoryAsync(symbol, from, now, workCt);
                }

                foreach (var b in buckets)
                {
                    fetched.Add((id, b));
                }

                return buckets.Count;
            },
            ex => VenuePenalty.Apply(_gate, ex),
            SweepFailure.Isolate,
            ct).ConfigureAwait(false);

        if (result.Failed > 0 && result.Written == 0 && result.LastError is not null)
        {
            throw new InvalidOperationException($"every symbol failed; last: {result.LastError.Message}", result.LastError);
        }

        // The venues with no series of their own: one sampled bucket per symbol from the ticker the
        // snapshot path already reads, floored onto the grid so repeated passes inside one bucket
        // collide on the primary key rather than multiplying rows.
        if (fetched.IsEmpty)
        {
            foreach (var t in await _adapter.GetTickersAsync(ct))
            {
                // Both halves are required and neither is guaranteed any more: a venue that
                // publishes no open interest at all (spot, and any vault-backed perp that reports
                // only notional) now reaches here with NULL instead of NaN, and a bucket needs an
                // instant as much as it needs a number. No observation, no bucket — the gap is the
                // honest record, and it is the same rule the snapshot path applies per column.
                if (!idBySymbol.TryGetValue(t.ExchangeSymbol, out var id)
                    || t.OpenInterest is not { } openInterest
                    || double.IsNaN(openInterest)
                    || t.OpenInterestAt is not { } openInterestAt)
                {
                    continue;
                }

                fetched.Add((id, new OpenInterestBucket(
                    t.ExchangeSymbol, SampledBucketSeconds, FloorTo(openInterestAt, SampledBucketSeconds),
                    Open: null, High: null, Low: null, Close: openInterest, Quote: null, Source: "rest")));
            }
        }

        if (fetched.IsEmpty)
        {
            return 0;
        }

        var receivedAt = _clock.GetUtcNow();
        var rows = fetched
            .Select(f => new OiRow(
                f.Id, f.Bucket.IntervalSeconds, f.Bucket.BucketTime,
                f.Bucket.Open, f.Bucket.High, f.Bucket.Low, f.Bucket.Close, f.Bucket.Quote,
                receivedAt, f.Bucket.Source))
            .ToList();

        await using var tx = await conn.BeginTransactionAsync(ct);
        await BulkJson.WriteAsync(
            conn, tx,
            """
            insert into open_interest_history (
                exchange_instrument_id, interval_s, bucket_time,
                oi_open, oi_high, oi_low, oi_close, oi_quote, received_at, source)
            select exchange_instrument_id, interval_s, bucket_time,
                   oi_open, oi_high, oi_low, oi_close, oi_quote, received_at, source
              from jsonb_to_recordset(@rows) as x(
                   exchange_instrument_id integer, interval_s integer, bucket_time timestamptz,
                   oi_open numeric, oi_high numeric, oi_low numeric, oi_close numeric,
                   oi_quote numeric, received_at timestamptz, source text)
            -- A bucket re-fetched later is the venue restating the same window, and the newer answer
            -- is the better one: an analytics bucket is only final once its window has closed.
            on conflict (exchange_instrument_id, interval_s, bucket_time) do update set
                oi_open     = excluded.oi_open,
                oi_high     = excluded.oi_high,
                oi_low      = excluded.oi_low,
                oi_close    = excluded.oi_close,
                oi_quote    = excluded.oi_quote,
                received_at = excluded.received_at,
                source      = excluded.source
            """,
            rows, ct);

        await Coverage.WriteAsync(
            conn, tx, "open_interest", now, receivedAt,
            rows.ConvertAll(r => (r.exchange_instrument_id, r.bucket_time)), ct);

        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private static DateTimeOffset FloorTo(DateTimeOffset at, int seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / seconds * seconds);

    private sealed record OiRow(
        int exchange_instrument_id,
        int interval_s,
        DateTimeOffset bucket_time,
        double? oi_open,
        double? oi_high,
        double? oi_low,
        double oi_close,
        double? oi_quote,
        DateTimeOffset received_at,
        string source);
}

using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Fills <c>funding_rate_history</c>. The live snapshot already carries the current rate; this keeps
/// the historical series, which — unlike OI or the order book — the venue does serve back in time,
/// so a fresh instance can back-fill it and a gap can be repaired. Appends what is missing
/// (<c>on conflict do nothing</c>); a re-run is free.
///
/// One REST call per instrument, same as <see cref="DepthCollector"/> and
/// <see cref="CandleCollector"/> — on WEEX that is on the order of a thousand calls a pass. Every
/// one of them goes through the venue's shared <see cref="VenueGate"/> (0021): this loop used to
/// issue them unpaced while depth alone respected the ceiling, which meant the venue-wide budget
/// the gate exists to enforce was routinely blown by the other two-thirds of the traffic.
/// </summary>
public sealed class FundingCollector
{
    // Back-fill targets: not delisted, and not turned off by an operator.
    internal const string TargetInstrumentsSql =
        """
        select i.id,
               i.exchange_symbol,
               i.funding_interval_hours,
               (select max(f.funding_time)
                  from funding_rate_history f
                 where f.exchange_instrument_id = i.id) as latest
          from exchange_instrument i
         where i.segment_code = @code
           and i.status <> 'delisted'
           and i.collect = true
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly VenueGate _gate;

    public FundingCollector(IExchangeMarketData adapter, DbSettings settings, Db db, TimeProvider clock, VenueGate gate)
    {
        _adapter = adapter;
        _settings = settings;
        _db = db;
        _clock = clock;
        _gate = gate;
    }

    /// <summary>Returns the number of new funding rows written.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var floor = now - TimeSpan.FromHours((await _settings.CurrentAsync(ct)).DatasetSettingInt("funding", "backfill_hours"));

        await using var conn = await _db.OpenAsync(ct);

        var observed = new System.Collections.Concurrent.ConcurrentBag<(int Id, DateTimeOffset At)>();

        var targets = (await conn.QueryAsync<(int Id, string Symbol, short? FundingIntervalHours, DateTimeOffset? Latest)>(new CommandDefinition(
            TargetInstrumentsSql,
            new { code = _adapter.SegmentCode },
            cancellationToken: ct))).ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        // One connection for the pass, as in DepthCollector: the writes are milliseconds against a
        // network call of hundreds, so a single write lane behind the fetches costs nothing and keeps
        // this loop's footprint on the pool at exactly one connection. Opening a connection per
        // worker would trade a venue-bound pass for a pool-bound one.
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
                var (id, symbol, fundingIntervalHours, latest) = target;

                // Same signal CandleCollector uses: nothing stored yet for this instrument means
                // this pull reaches back to floor, i.e. a genuine catch-up rather than the normal
                // roll-forward from the last payment.
                var source = latest is null ? "backfill" : "rest";

                // From the newest stored payment (nothing before it can be missing), bounded so a
                // first run cannot ask a venue for years of history.
                var from = latest ?? floor;
                if (from < floor)
                {
                    from = floor;
                }

                IReadOnlyList<FundingRate> rates;
                using (await _gate.AcquireAsync(workCt).ConfigureAwait(false))
                {
                    rates = await _adapter.GetFundingHistoryAsync(symbol, from, now, workCt);
                }

                if (rates.Count == 0)
                {
                    return 0;
                }

                await writeLane.WaitAsync(workCt).ConfigureAwait(false);
                try
                {
                    var stored = 0;
                    var receivedAt = _clock.GetUtcNow();
                    foreach (var rate in rates)
                    {
                        observed.Add((id, rate.FundingTime));
                        stored += await conn.ExecuteAsync(new CommandDefinition(
                            """
                            insert into funding_rate_history (
                                exchange_instrument_id, funding_time, rate,
                                received_at, source, funding_interval_hours)
                            values (@Id, @FundingTime, @Rate, @ReceivedAt, @Source, @FundingIntervalHours)
                            on conflict (exchange_instrument_id, funding_time) do nothing
                            """,
                            new
                            {
                                Id = id, rate.FundingTime, rate.Rate,
                                ReceivedAt = receivedAt, Source = source,
                                FundingIntervalHours = fundingIntervalHours,
                            },
                            cancellationToken: workCt));
                    }

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

        // What each symbol's history request actually returned. Written after the per-row inserts
        // above, which are already committed individually — this loop has no transaction of its own
        // to join, so the claim follows the data rather than travelling with it.
        await Coverage.WriteAsync(
            conn, null, "funding", now, _clock.GetUtcNow(), observed.ToList(), ct);

        return written;
    }
}

using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Fills <c>funding_rate_history</c>. The live snapshot already carries the current rate; this keeps
/// the historical series, which — unlike OI or the order book — the venue does serve back in time,
/// so a fresh instance can back-fill it and a gap can be repaired. Appends what is missing
/// (<c>on conflict do nothing</c>); a re-run is free.
/// </summary>
public sealed class FundingCollector
{
    // Back-fill targets: not delisted, and not turned off by an operator.
    internal const string TargetInstrumentsSql =
        """
        select i.id,
               i.exchange_symbol,
               (select max(f.funding_time)
                  from funding_rate_history f
                 where f.exchange_instrument_id = i.id) as latest
          from exchange_instrument i
         where i.exchange_code = @code
           and i.status <> 'delisted'
           and i.collect = true
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;

    public FundingCollector(IExchangeMarketData adapter, DbSettings settings, Db db, TimeProvider clock)
    {
        _adapter = adapter;
        _settings = settings;
        _db = db;
        _clock = clock;
    }

    /// <summary>Returns the number of new funding rows written.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var floor = now - TimeSpan.FromHours((await _settings.CurrentAsync(ct)).CollectionSettingInt("funding", "backfill_hours"));

        await using var conn = await _db.OpenAsync(ct);

        var targets = (await conn.QueryAsync<(int Id, string Symbol, DateTimeOffset? Latest)>(new CommandDefinition(
            TargetInstrumentsSql,
            new { code = _adapter.ExchangeCode },
            cancellationToken: ct))).ToList();

        var written = 0;

        // One venue symbol whose endpoint is broken (WEEX serves 400 for a live market's
        // candles) must not starve every symbol after it. Per-symbol isolation: remember the
        // failure, keep walking; only an all-symbols failure fails the pass — that is an
        // outage, not a pothole.
        var failed = 0;
        Exception? lastError = null;
        foreach (var (id, symbol, latest) in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {

            // From the newest stored payment (nothing before it can be missing), bounded so a first
            // run cannot ask a venue for years of history.
            var from = latest ?? floor;
            if (from < floor)
            {
                from = floor;
            }

            var rates = await _adapter.GetFundingHistoryAsync(symbol, from, now, ct);
            foreach (var rate in rates)
            {
                written += await conn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into funding_rate_history (exchange_instrument_id, funding_time, rate)
                    values (@Id, @FundingTime, @Rate)
                    on conflict (exchange_instrument_id, funding_time) do nothing
                    """,
                    new { Id = id, rate.FundingTime, rate.Rate },
                    cancellationToken: ct));
            }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                lastError = ex;
            }
        }

        if (failed > 0 && written == 0 && lastError is not null)
        {
            throw new InvalidOperationException($"every symbol failed; last: {lastError.Message}", lastError);
        }

        return written;
    }
}

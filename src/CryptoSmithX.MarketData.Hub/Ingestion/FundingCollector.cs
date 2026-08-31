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
        var floor = now - TimeSpan.FromHours((await _settings.CurrentAsync(ct)).FundingBackfillHours);

        await using var conn = await _db.OpenAsync(ct);

        var targets = (await conn.QueryAsync<(int Id, string Symbol, DateTimeOffset? Latest)>(new CommandDefinition(
            """
            select i.id,
                   i.exchange_symbol,
                   (select max(f.funding_time)
                      from funding_rate_history f
                     where f.exchange_instrument_id = i.id) as latest
              from exchange_instrument i
             where i.exchange_code = @code
               and i.status <> 'delisted'
            """,
            new { code = _adapter.ExchangeCode },
            cancellationToken: ct))).ToList();

        var written = 0;
        foreach (var (id, symbol, latest) in targets)
        {
            ct.ThrowIfCancellationRequested();

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

        return written;
    }
}

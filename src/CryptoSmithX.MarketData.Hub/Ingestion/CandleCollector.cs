using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Pulls closed 1-minute bars per instrument, starting after the newest bar already stored and
/// bounded so a first run cannot ask a venue for a year of history.
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
         where i.exchange_code = @code
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

    public CandleCollector(IExchangeMarketData adapter, DbSettings settings, Db db, TimeProvider clock)
    {
        _adapter = adapter;
        _settings = settings;
        _db = db;
        _clock = clock;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var floor = now - TimeSpan.FromHours((await _settings.CurrentAsync(ct)).CollectionSettingInt("candles", "backfill_hours"));

        await using var conn = await _db.OpenAsync(ct);
        await Partitions.EnsureAsync(conn, now, ct);

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

            // Re-ask for the newest stored minute as well: a venue that back-fills a late bar
            // then has a chance to correct it, and the rollup repairs the parents from there.
            var from = latest is null ? floor : latest.Value;
            if (from < floor)
            {
                from = floor;
            }

            var candles = await _adapter.GetCandles1mAsync(symbol, from, now, ct);
            if (candles.Count == 0)
            {
                continue;
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
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
                    tx, cancellationToken: ct));
                written++;
            }

            await tx.CommitAsync(ct);
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

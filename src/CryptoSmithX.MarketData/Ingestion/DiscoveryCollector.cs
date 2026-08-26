using CryptoSmithX.Exchanges;
using CryptoSmithX.Exchanges.Market;
using CryptoSmithX.MarketData.Options;
using CryptoSmithX.MarketData.Storage;
using Dapper;

namespace CryptoSmithX.MarketData.Ingestion;

/// <summary>
/// Keeps <c>exchange_instrument</c> in step with the venue's listing. Runs before any snapshot so
/// there is always a row to point a snapshot at.
/// </summary>
public sealed class DiscoveryCollector
{
    private readonly IExchangeMarketData _adapter;
    private readonly ExchangeOptions _exchange;
    private readonly MarketDataOptions _options;
    private readonly Db _db;

    public DiscoveryCollector(
        IExchangeMarketData adapter, ExchangeOptions exchange, MarketDataOptions options, Db db)
    {
        _adapter = adapter;
        _exchange = exchange;
        _options = options;
        _db = db;
    }

    /// <summary>Returns the number of instruments the venue listed.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var instruments = await _adapter.GetInstrumentsAsync(ct);

        var quotes = _exchange.QuoteAssets;
        var blacklist = _exchange.Blacklist;
        var kept = instruments
            .Where(i => quotes.Count == 0 || quotes.Contains(i.QuoteAsset, StringComparer.OrdinalIgnoreCase))
            .Where(i => !blacklist.Contains(i.ExchangeSymbol, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var i in kept)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into exchange_instrument (
                    exchange_code, exchange_symbol, base_asset, quote_asset, contract_multiplier,
                    price_step, qty_step, min_qty, min_notional, funding_interval_hours,
                    status, status_changed_at, first_seen_at, last_seen_at, raw_json, updated_at)
                values (
                    @ExchangeCode, @ExchangeSymbol, @BaseAsset, @QuoteAsset, @ContractMultiplier,
                    @PriceStep, @QtyStep, @MinQty, @MinNotional, @FundingIntervalHours,
                    @Status, @Now, @Now, @Now, @RawJson::jsonb, @Now)
                on conflict (exchange_code, exchange_symbol) do update set
                    base_asset             = excluded.base_asset,
                    quote_asset            = excluded.quote_asset,
                    contract_multiplier    = excluded.contract_multiplier,
                    price_step             = excluded.price_step,
                    qty_step               = excluded.qty_step,
                    min_qty                = excluded.min_qty,
                    min_notional           = excluded.min_notional,
                    funding_interval_hours = excluded.funding_interval_hours,
                    status                 = excluded.status,
                    -- only a real change moves the clock
                    status_changed_at      = case when exchange_instrument.status is distinct from excluded.status
                                                  then excluded.status_changed_at
                                                  else exchange_instrument.status_changed_at end,
                    last_seen_at           = excluded.last_seen_at,
                    raw_json               = excluded.raw_json,
                    updated_at             = excluded.updated_at
                """,
                new
                {
                    ExchangeCode = _adapter.ExchangeCode,
                    i.ExchangeSymbol,
                    i.BaseAsset,
                    i.QuoteAsset,
                    i.ContractMultiplier,
                    i.PriceStep,
                    i.QtyStep,
                    i.MinQty,
                    i.MinNotional,
                    i.FundingIntervalHours,
                    Status = i.Status.ToDb(),
                    Now = now,
                    i.RawJson,
                },
                tx,
                cancellationToken: ct));
        }

        // Gone for several rounds in a row is a delisting. Age of last_seen_at is used rather than
        // an in-memory miss counter so a restart does not forget what it had seen.
        var missedFor = _options.DiscoveryInterval * _options.DelistAfterMissedDiscoveries;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            update exchange_instrument
               set status            = 'delisted',
                   status_changed_at = @Now,
                   updated_at        = @Now
             where exchange_code = @ExchangeCode
               and status <> 'delisted'
               and last_seen_at < @Cutoff
            """,
            new { ExchangeCode = _adapter.ExchangeCode, Now = now, Cutoff = now - missedFor },
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        return kept.Count;
    }
}

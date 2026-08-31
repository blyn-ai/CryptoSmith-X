using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Hub.Options;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Keeps <c>exchange_instrument</c> in step with the venue's listing. Runs before any snapshot so
/// there is always a row to point a snapshot at. Also owns the raw→canonical asset resolve that
/// used to live inside each adapter: the venue's raw base string is mapped against the
/// <c>asset_alias</c> table, unknown assets are auto-registered, and the alias multiplier folds
/// into the instrument's own.
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
            .Where(i => quotes.Count == 0 || quotes.Contains(i.QuoteAssetRaw, StringComparer.OrdinalIgnoreCase))
            .Where(i => !blacklist.Contains(i.ExchangeSymbol, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // One batch read of every alias that could apply to this exchange (its own + globals),
        // not a query per instrument. Case-insensitive on the raw, the way venues vary casing.
        var aliasRows = await conn.QueryAsync<(string? ExchangeCode, string Alias, string AssetCode, decimal Multiplier)>(
            new CommandDefinition(
                "select exchange_code, alias, asset_code, multiplier from asset_alias "
                + "where exchange_code = @code or exchange_code is null",
                new { code = _adapter.ExchangeCode },
                tx,
                cancellationToken: ct));

        var exchangeAliases = new Dictionary<string, AliasHit>(StringComparer.OrdinalIgnoreCase);
        var globalAliases = new Dictionary<string, AliasHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exchangeCode, alias, assetCode, multiplier) in aliasRows)
        {
            var target = exchangeCode is null ? globalAliases : exchangeAliases;
            target[alias] = new AliasHit(assetCode, multiplier);
        }

        // Resolve every raw base to its canon + effective multiplier before touching the table.
        var resolved = kept
            .Select(i =>
            {
                var hit = AssetResolver.Resolve(i.BaseAssetRaw, exchangeAliases, globalAliases);
                return (Instrument: i, Canon: hit.Canon, Multiplier: i.ContractMultiplier * hit.Multiplier);
            })
            .ToList();

        // Auto-register any canon that has no asset row yet (identity misses; alias targets already
        // exist by their FK). One batch insert before the instrument upserts satisfy their own FK.
        var canons = resolved.Select(r => r.Canon).Distinct(StringComparer.Ordinal).ToArray();
        await conn.ExecuteAsync(new CommandDefinition(
            "insert into asset (code, note) select c, 'auto-registered' from unnest(@Canons) as c "
            + "on conflict (code) do nothing",
            new { Canons = canons },
            tx,
            cancellationToken: ct));

        foreach (var (i, canon, multiplier) in resolved)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into exchange_instrument (
                    exchange_code, exchange_symbol, base_asset, base_asset_raw,
                    quote_asset, quote_asset_raw, contract_multiplier,
                    price_step, qty_step, min_qty, min_notional, funding_interval_hours,
                    listed_at, status, status_changed_at, first_seen_at, last_seen_at, raw_json, updated_at)
                values (
                    @ExchangeCode, @ExchangeSymbol, @BaseAsset, @BaseAssetRaw,
                    @QuoteAsset, @QuoteAssetRaw, @ContractMultiplier,
                    @PriceStep, @QtyStep, @MinQty, @MinNotional, @FundingIntervalHours,
                    @ListedAt, @Status, @Now, @Now, @Now, @RawJson::jsonb, @Now)
                on conflict (exchange_code, exchange_symbol) do update set
                    -- canon and multiplier are re-applied so a discovery pass repairs them after an
                    -- admin edits an alias; base_asset_raw is what the venue actually sent.
                    base_asset             = excluded.base_asset,
                    base_asset_raw         = excluded.base_asset_raw,
                    quote_asset            = excluded.quote_asset,
                    quote_asset_raw        = excluded.quote_asset_raw,
                    contract_multiplier    = excluded.contract_multiplier,
                    price_step             = excluded.price_step,
                    qty_step               = excluded.qty_step,
                    min_qty                = excluded.min_qty,
                    min_notional           = excluded.min_notional,
                    funding_interval_hours = excluded.funding_interval_hours,
                    listed_at              = excluded.listed_at,
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
                    BaseAsset = canon,
                    i.BaseAssetRaw,
                    // No quote aliases in V1, so the quote canon is its raw.
                    QuoteAsset = i.QuoteAssetRaw,
                    i.QuoteAssetRaw,
                    ContractMultiplier = multiplier,
                    i.PriceStep,
                    i.QtyStep,
                    i.MinQty,
                    i.MinNotional,
                    i.FundingIntervalHours,
                    i.ListedAt,
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

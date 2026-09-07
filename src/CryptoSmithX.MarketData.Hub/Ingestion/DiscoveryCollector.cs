using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Keeps <c>exchange_instrument</c> in step with the venue's listing. Runs before any snapshot so
/// there is always a row to point a snapshot at. Also owns the raw→canonical asset resolve that
/// used to live inside each adapter: the venue's raw base string is mapped against the
/// <c>asset_alias</c> table, unknown assets are auto-registered, and the alias multiplier folds
/// into the instrument's own.
/// <para>
/// Since 0029 it also stands at the collect gate: a listing seen for the first time arrives
/// switched OFF unless its base asset is auto-approved (<c>asset.auto_collect</c>). It writes
/// <c>collect</c> on the insert path only — see <see cref="DecideCollectOnInsert"/> and the comment
/// at the foot of <see cref="UpsertInstrumentSql"/> for why the update path must never touch it.
/// </para>
/// </summary>
public sealed class DiscoveryCollector
{
    // The base assets whose new listings pass the gate without a human (0029 asset.auto_collect).
    // Read once per pass, in the pass's own transaction, like the alias table above it: a listing
    // and the flag that decides it are then read from one consistent view of the database.
    internal const string AutoCollectAssetsSql = "select code from asset where auto_collect";

    // The listing upsert. Held as a const for the same reason as SnapshotCollector's target query:
    // the collect column's PRESENCE in the insert list and its ABSENCE from the update list are the
    // whole gate, and a test can only guard what it can read.
    internal const string UpsertInstrumentSql =
        """
        insert into exchange_instrument (
            segment_code, exchange_symbol, base_asset, base_asset_raw,
            quote_asset, quote_asset_raw, contract_multiplier,
            price_step, qty_step, min_qty, min_notional, funding_interval_hours,
            listed_at, status, status_changed_at, first_seen_at, last_seen_at, raw_json, updated_at,
            collect)
        values (
            @SegmentCode, @ExchangeSymbol, @BaseAsset, @BaseAssetRaw,
            @QuoteAsset, @QuoteAssetRaw, @ContractMultiplier,
            @PriceStep, @QtyStep, @MinQty, @MinNotional, @FundingIntervalHours,
            @ListedAt, @Status, @Now, @Now, @Now, @RawJson::jsonb, @Now,
            @Collect)
        on conflict (segment_code, exchange_symbol) do update set
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
            -- collect is ABSENT from this list ON PURPOSE, and must stay absent. It used to be
            -- absent by omission, which is a property nobody can see and the next reader will
            -- "fix" for consistency with the insert list three lines up. What that edit would do:
            -- every discovery pass, every few minutes, would stamp the auto-approve answer back
            -- over the operator's. The 1864 instruments an admin has just switched off would come
            -- back on for the 25 approved assets and be re-written off for the rest, and the
            -- toggle in the admin UI would appear to do nothing at all — a change reverted by a
            -- background loop, with the audit columns still swearing a human made the last one.
            -- collect is OUR decision; discovery reports the VENUE's. It writes the venue's
            -- columns and stays off ours. See DecideCollectOnInsert for the other half.
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly DbSettings _settings;
    private readonly Db _db;

    public DiscoveryCollector(IExchangeMarketData adapter, DbSettings settings, Db db)
    {
        _adapter = adapter;
        _settings = settings;
        _db = db;
    }

    /// <summary>Returns the number of instruments the venue listed.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var snapshot = await _settings.CurrentAsync(ct);
        var config = snapshot.Exchange(_adapter.SegmentCode);
        if (config is null)
        {
            // The exchange was removed or disabled out from under us; do nothing this pass.
            return 0;
        }

        var instruments = await _adapter.GetInstrumentsAsync(ct);

        var quotes = config.QuoteAssets;
        var blacklist = config.Blacklist;
        var kept = instruments
            .Where(i => quotes.Length == 0 || quotes.Contains(i.QuoteAssetRaw, StringComparer.OrdinalIgnoreCase))
            .Where(i => !blacklist.Contains(i.ExchangeSymbol, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // One batch read of every alias that could apply to this exchange (its own + globals),
        // not a query per instrument. Case-insensitive on the raw, the way venues vary casing.
        var aliasRows = await conn.QueryAsync<(string? SegmentCode, string Alias, string AssetCode, decimal Multiplier)>(
            new CommandDefinition(
                "select segment_code, alias, asset_code, multiplier from asset_alias "
                + "where segment_code = @code or segment_code is null",
                new { code = _adapter.SegmentCode },
                tx,
                cancellationToken: ct));

        var exchangeAliases = new Dictionary<string, AliasHit>(StringComparer.OrdinalIgnoreCase);
        var globalAliases = new Dictionary<string, AliasHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (segmentCode, alias, assetCode, multiplier) in aliasRows)
        {
            var target = segmentCode is null ? globalAliases : exchangeAliases;
            target[alias] = new AliasHit(assetCode, multiplier);
        }

        // Ordinal, not case-insensitive, and that is the deliberate choice: asset.code is a text
        // primary key, so 'BTC' and 'btc' are two different rows and base_asset's FK points at
        // exactly one of them. Matching loosely here would let the flag set on 'BTC' approve a
        // listing whose FK resolves to some other row — the flag would govern a row it is not on.
        var autoCollectAssets = (await conn.QueryAsync<string>(new CommandDefinition(
                AutoCollectAssetsSql,
                transaction: tx,
                cancellationToken: ct)))
            .ToHashSet(StringComparer.Ordinal);

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
                UpsertInstrumentSql,
                new
                {
                    SegmentCode = _adapter.SegmentCode,
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
                    Collect = DecideCollectOnInsert(canon, autoCollectAssets),
                },
                tx,
                cancellationToken: ct));
        }

        // Gone for several rounds in a row is a delisting. Age of last_seen_at is used rather than
        // an in-memory miss counter so a restart does not forget what it had seen.
        var missedFor = snapshot.DatasetInterval(_adapter.SegmentCode, "discovery")
            * snapshot.DatasetSettingInt("discovery", "delist_after_missed_discoveries");
        await conn.ExecuteAsync(new CommandDefinition(
            """
            update exchange_instrument
               set status            = 'delisted',
                   status_changed_at = @Now,
                   updated_at        = @Now
             where segment_code = @SegmentCode
               and status <> 'delisted'
               and last_seen_at < @Cutoff
            """,
            new { SegmentCode = _adapter.SegmentCode, Now = now, Cutoff = now - missedFor },
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        return kept.Count;
    }

    /// <summary>
    /// The value <c>collect</c> takes when a listing is seen for the FIRST time. Pure so the gate
    /// can be driven without a database; the SQL above decides only WHERE it applies (insert, never
    /// update).
    /// </summary>
    /// <remarks>
    /// This is a stamp taken at arrival, not a standing rule re-applied every pass — and that is a
    /// position, not an accident.
    /// <para>
    /// It means an asset added to the auto-approve list LATER does not reach back and switch on the
    /// listings already sitting undecided. Add ONDO today and tomorrow's ONDO listing arrives
    /// collected; the twelve ONDO listings already waiting stay waiting until someone approves
    /// them, which the admin's per-instrument toggle already does and records.
    /// </para>
    /// <para>
    /// REJECTED: re-evaluating undecided rows (<c>collect_changed_at is null</c>) on every pass, so
    /// the list reads as a live rule. It is genuinely the friendlier story — the list would then
    /// mean one thing at all times, "these assets are collected", with no "you ticked it too late"
    /// surprise and no way for the flag and the book to disagree. Three costs sank it. First, it
    /// writes <c>collect</c> with nobody's name on it: the audit columns exist to say who decided,
    /// and a background loop flipping rows on leaves them null, so the row would then be collected
    /// AND still read as NEW. Second, the blast radius of a checkbox becomes unbounded and
    /// invisible — ticking one asset starts real collection on every undecided listing of it across
    /// every venue at once, with no count shown and no confirmation, and unticking it silently
    /// stops collection on rows that were being collected. Third, NEW stops being a state and
    /// becomes a race: a row reads NEW on Monday and collected on Tuesday with nothing in the row
    /// explaining the change. The stamp keeps <c>collect</c> meaning exactly one thing — somebody
    /// or some rule decided this, once, at a moment we can name.
    /// </para>
    /// <para>
    /// A RE-LISTING takes the update path, so none of this applies to it. Delisting never deletes
    /// the row: the sweep below only sets <c>status = 'delisted'</c>, so an instrument that goes
    /// away and comes back hits <c>on conflict do update</c> and keeps whatever <c>collect</c> it
    /// had — collected stays collected and resumes on its own, an operator's off stays off, and one
    /// that was never decided is still NEW rather than sneaking through the gate on its return.
    /// That is wanted. A delisting is the venue's statement about the venue, not ours about us, and
    /// it is inferred from ABSENCE over several passes — a flaky endpoint can produce one. Letting
    /// a return re-run the gate would mean venue flakiness could quietly re-approve an instrument
    /// an admin had switched off, or demand a human re-approve BTC after a maintenance window.
    /// </para>
    /// </remarks>
    internal static bool DecideCollectOnInsert(string canon, IReadOnlySet<string> autoCollectAssets) =>
        autoCollectAssets.Contains(canon);
}

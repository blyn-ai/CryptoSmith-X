using System.Globalization;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub;

/// <summary>
/// The Hub's configuration, read from the database rather than appsettings. A plain class like
/// <see cref="Db"/>, not IOptions: settings change at runtime from the admin UI, so a static
/// options object would be a lie. A snapshot of the whole <c>setting</c> table, every <c>exchange</c>
/// row, the <c>collection</c> catalogue, <c>collection_setting</c> and the full <c>exchange_collection</c>
/// matrix is cached for ~30 s; loops read the cached copy on every iteration, so an edit in the UI
/// takes effect within that window plus one loop interval — no restart.
/// </summary>
public sealed class DbSettings
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SettingsSnapshot? _cached;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public DbSettings(Db db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The last loaded snapshot, for synchronous readers (interval providers). Never null
    /// once <see cref="CurrentAsync"/> has run at least once — the supervisor guarantees that.</summary>
    public SettingsSnapshot Latest =>
        _cached ?? throw new InvalidOperationException("Settings have not been loaded yet.");

    /// <summary>The current snapshot, reloaded from the database if the cache is older than the TTL.</summary>
    public async Task<SettingsSnapshot> CurrentAsync(CancellationToken ct)
    {
        if (_cached is not null && _clock.GetUtcNow() - _loadedAt < Ttl)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && _clock.GetUtcNow() - _loadedAt < Ttl)
            {
                return _cached;
            }

            var loaded = await LoadAsync(ct);
            _cached = loaded;
            _loadedAt = _clock.GetUtcNow();
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SettingsSnapshot> LoadAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        var settings = (await conn.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "select key, value from setting", cancellationToken: ct)))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        var exchanges = (await conn.QueryAsync<ExchangeConfig>(new CommandDefinition(
            """
            select code         as "Code",
                   adapter      as "Adapter",
                   base_url     as "BaseUrl",
                   charts_url   as "ChartsUrl",
                   ws_url       as "WsUrl",
                   quote_assets as "QuoteAssets",
                   blacklist    as "Blacklist",
                   status       as "Status"
              from exchange
             order by code
            """, cancellationToken: ct))).ToList();

        var collections = (await conn.QueryAsync<CollectionDefaults>(new CommandDefinition(
            """
            select code                   as "Code",
                   kind                   as "Kind",
                   default_mode           as "DefaultMode",
                   default_interval_s     as "DefaultIntervalS",
                   default_retention_days as "DefaultRetentionDays"
              from collection
            """, cancellationToken: ct)))
            .ToDictionary(c => c.Code, StringComparer.Ordinal);

        var collectionSettings = (await conn.QueryAsync<(string Collection, string Key, string Value)>(
            new CommandDefinition(
                "select collection_code, key, value from collection_setting", cancellationToken: ct)))
            .ToDictionary(r => (r.Collection, r.Key), r => r.Value);

        var matrix = (await conn.QueryAsync<ExchangeCollectionRow>(new CommandDefinition(
            """
            select exchange_code   as "ExchangeCode",
                   collection_code as "CollectionCode",
                   mode            as "Mode",
                   interval_s      as "IntervalS",
                   retention_days  as "RetentionDays",
                   transport       as "Transport"
              from exchange_collection
            """, cancellationToken: ct)))
            .ToDictionary(r => (r.ExchangeCode, r.CollectionCode));

        return new SettingsSnapshot(settings, exchanges, collections, collectionSettings, matrix);
    }
}

/// <summary>
/// One exchange's configuration row — everything that stays on <c>exchange</c> (how to reach it, and
/// whether it runs at all). What and how often to collect moved to <c>exchange_collection</c> (0014).
/// Property-init rather than positional so Dapper materialises it through property setters — its
/// constructor path does not bind the text[] columns to string[] parameters.
/// </summary>
public sealed record ExchangeConfig
{
    public string Code { get; init; } = "";
    public string Adapter { get; init; } = "";
    public string? BaseUrl { get; init; }
    public string? ChartsUrl { get; init; }
    public string? WsUrl { get; init; }
    public string[] QuoteAssets { get; init; } = [];
    public string[] Blacklist { get; init; } = [];
    public string Status { get; init; } = "";
}

/// <summary>One row of the <c>collection</c> catalogue — the terminal default of the cascade.</summary>
public sealed record CollectionDefaults
{
    public string Code { get; init; } = "";
    public string Kind { get; init; } = "";
    public string DefaultMode { get; init; } = "";
    public int? DefaultIntervalS { get; init; }
    public int? DefaultRetentionDays { get; init; }
}

/// <summary>One cell of the exchange×collection matrix — always present for every pair (0014).</summary>
public sealed record ExchangeCollectionRow
{
    public string ExchangeCode { get; init; } = "";
    public string CollectionCode { get; init; } = "";
    public string Mode { get; init; } = "";
    public int? IntervalS { get; init; }
    public int? RetentionDays { get; init; }
    public string? Transport { get; init; }
}

/// <summary>An immutable read of the whole settings surface: global values, every exchange, the
/// collection catalogue and the full policy matrix.</summary>
public sealed class SettingsSnapshot
{
    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly IReadOnlyDictionary<string, CollectionDefaults> _collections;
    private readonly IReadOnlyDictionary<(string Collection, string Key), string> _collectionSettings;
    private readonly IReadOnlyDictionary<(string Exchange, string Collection), ExchangeCollectionRow> _matrix;

    public SettingsSnapshot(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyList<ExchangeConfig> exchanges,
        IReadOnlyDictionary<string, CollectionDefaults> collections,
        IReadOnlyDictionary<(string Collection, string Key), string> collectionSettings,
        IReadOnlyDictionary<(string Exchange, string Collection), ExchangeCollectionRow> matrix)
    {
        _settings = settings;
        Exchanges = exchanges;
        _collections = collections;
        _collectionSettings = collectionSettings;
        _matrix = matrix;
    }

    public IReadOnlyList<ExchangeConfig> Exchanges { get; }

    public ExchangeConfig? Exchange(string code) =>
        Exchanges.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.Ordinal));

    public IReadOnlyCollection<CollectionDefaults> Collections => (IReadOnlyCollection<CollectionDefaults>)_collections.Values;

    // WS honesty knobs (0010): how fresh a cached WS record must be to be served, and the
    // REST cross-check cadence and drift threshold that catch a silently frozen book. These stay
    // global (0014): they are about the transport, not about any one collection.
    public TimeSpan WsStaleAfter => TimeSpan.FromSeconds(GetInt("ws_stale_after_s"));
    public TimeSpan WsCrosscheckInterval => TimeSpan.FromSeconds(GetInt("ws_crosscheck_interval_s"));
    public int WsCrosscheckDriftBps => GetInt("ws_crosscheck_drift_bps");

    /// <summary>
    /// The matrix cell for one exchange×collection pair, or null if the pair genuinely has no row —
    /// which should not happen for a real exchange once 0014's backfill has run, but a brand-new
    /// exchange added by hand outside a migration would hit this until its matrix row exists.
    /// </summary>
    public ExchangeCollectionRow? Cell(string exchangeCode, string collectionCode) =>
        _matrix.GetValueOrDefault((exchangeCode, collectionCode));

    /// <summary>The human decision for this pair — 'disabled' / 'on_demand' / 'collect'. Mode has no
    /// cascade: <c>exchange_collection.mode</c> is never null, unlike interval/retention.</summary>
    public string Mode(string exchangeCode, string collectionCode) =>
        Cell(exchangeCode, collectionCode)?.Mode
        ?? _collections.GetValueOrDefault(collectionCode)?.DefaultMode
        ?? "disabled";

    /// <summary>Cascade: exchange_collection.interval_s -> collection.default_interval_s. Throws if
    /// neither level has a value — a collection whose mode can be 'collect' must always resolve one.</summary>
    public TimeSpan CollectionInterval(string exchangeCode, string collectionCode)
    {
        var fromCell = Cell(exchangeCode, collectionCode)?.IntervalS;
        var fromCollection = _collections.GetValueOrDefault(collectionCode)?.DefaultIntervalS;
        var seconds = fromCell ?? fromCollection
            ?? throw new InvalidOperationException(
                $"No interval resolved for {exchangeCode}/{collectionCode} — neither the matrix cell nor "
                + "the collection default is set.");
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Retention for a collection, collection-level only — deliberately NOT overridable per exchange.
    /// <c>market_snapshot</c> partitions are shared by every exchange; dropping a partition cannot
    /// spare one exchange's rows, so a per-exchange <c>exchange_collection.retention_days</c> value
    /// is recorded (visible as the operator's intent) but not honoured here. See the 0014 migration
    /// header for the full reasoning. null = never rotated.
    /// </summary>
    public int? CollectionRetentionDays(string collectionCode) =>
        _collections.GetValueOrDefault(collectionCode)?.DefaultRetentionDays;

    /// <summary>A <c>collection_setting</c> int value — the four knobs that are neither an interval
    /// nor a retention (backfill windows, the delist counter).</summary>
    public int CollectionSettingInt(string collectionCode, string key) =>
        int.Parse(RequireCollectionSetting(collectionCode, key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    public IReadOnlyList<int> CollectionSettingIntList(string collectionCode, string key) =>
        RequireCollectionSetting(collectionCode, key)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .ToList();

    private string RequireCollectionSetting(string collectionCode, string key) =>
        _collectionSettings.TryGetValue((collectionCode, key), out var value)
            ? value
            : throw new InvalidOperationException($"collection_setting '{collectionCode}/{key}' is missing from the database.");

    public int GetInt(string key) =>
        int.Parse(Require(key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private string Require(string key) =>
        _settings.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Setting '{key}' is missing from the database.");
}

using System.Globalization;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub;

/// <summary>
/// The Hub's configuration, read from the database rather than appsettings. A plain class like
/// <see cref="Db"/>, not IOptions: settings change at runtime from the admin UI, so a static
/// options object would be a lie. A snapshot of the whole <c>setting</c> table and every
/// <c>exchange</c> row is cached for ~30 s; loops read the cached copy on every iteration, so an
/// edit in the UI takes effect within that window plus one loop interval — no restart.
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
            select code                   as "Code",
                   adapter                as "Adapter",
                   base_url               as "BaseUrl",
                   charts_url             as "ChartsUrl",
                   quote_assets           as "QuoteAssets",
                   blacklist              as "Blacklist",
                   status                 as "Status",
                   snapshot_interval_s    as "SnapshotIntervalS",
                   candle_interval_s      as "CandleIntervalS",
                   discovery_interval_min as "DiscoveryIntervalMin",
                   funding_interval_min   as "FundingIntervalMin",
                   depth_interval_s       as "DepthIntervalS"
              from exchange
             order by code
            """, cancellationToken: ct))).ToList();

        return new SettingsSnapshot(settings, exchanges);
    }
}

/// <summary>
/// One exchange's configuration row. Interval overrides are null when the global wins. Property-init
/// rather than positional so Dapper materialises it through property setters — its constructor path
/// does not bind the text[] columns to string[] parameters.
/// </summary>
public sealed record ExchangeConfig
{
    public string Code { get; init; } = "";
    public string Adapter { get; init; } = "";
    public string? BaseUrl { get; init; }
    public string? ChartsUrl { get; init; }
    public string[] QuoteAssets { get; init; } = [];
    public string[] Blacklist { get; init; } = [];
    public string Status { get; init; } = "";
    public int? SnapshotIntervalS { get; init; }
    public int? CandleIntervalS { get; init; }
    public int? DiscoveryIntervalMin { get; init; }
    public int? FundingIntervalMin { get; init; }
    public int? DepthIntervalS { get; init; }
}

/// <summary>An immutable read of the whole settings surface. Global values plus every exchange.</summary>
public sealed class SettingsSnapshot
{
    private readonly IReadOnlyDictionary<string, string> _settings;

    public SettingsSnapshot(IReadOnlyDictionary<string, string> settings, IReadOnlyList<ExchangeConfig> exchanges)
    {
        _settings = settings;
        Exchanges = exchanges;
    }

    public IReadOnlyList<ExchangeConfig> Exchanges { get; }

    public ExchangeConfig? Exchange(string code) =>
        Exchanges.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.Ordinal));

    // Global settings (were MarketDataOptions).
    public int SnapshotRetentionDays => GetInt("snapshot_retention_days");
    public int CandleBackfillHours => GetInt("candle_backfill_hours");
    public int FundingBackfillHours => GetInt("funding_backfill_hours");
    public int DelistAfterMissedDiscoveries => GetInt("delist_after_missed_discoveries");
    public IReadOnlyList<int> DerivedTimeframes => GetIntList("derived_timeframes");

    // Effective per-exchange intervals: the exchange override, or the global if it is null.
    public TimeSpan SnapshotInterval(ExchangeConfig e) =>
        TimeSpan.FromSeconds(e.SnapshotIntervalS ?? GetInt("snapshot_interval_s"));
    public TimeSpan CandleInterval(ExchangeConfig e) =>
        TimeSpan.FromSeconds(e.CandleIntervalS ?? GetInt("candle_interval_s"));
    public TimeSpan DiscoveryInterval(ExchangeConfig e) =>
        TimeSpan.FromMinutes(e.DiscoveryIntervalMin ?? GetInt("discovery_interval_min"));
    public TimeSpan FundingInterval(ExchangeConfig e) =>
        TimeSpan.FromMinutes(e.FundingIntervalMin ?? GetInt("funding_interval_min"));
    public TimeSpan DepthInterval(ExchangeConfig e) =>
        TimeSpan.FromSeconds(e.DepthIntervalS ?? GetInt("depth_interval_s"));

    public int GetInt(string key) =>
        int.Parse(Require(key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    public IReadOnlyList<int> GetIntList(string key) =>
        Require(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .ToList();

    private string Require(string key) =>
        _settings.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Setting '{key}' is missing from the database.");
}

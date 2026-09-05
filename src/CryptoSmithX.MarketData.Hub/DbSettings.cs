using System.Globalization;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub;

/// <summary>
/// The Hub's configuration, read from the database rather than appsettings. A plain class like
/// <see cref="Db"/>, not IOptions: settings change at runtime from the admin UI, so a static
/// options object would be a lie. A snapshot of the whole <c>setting</c> table, every <c>exchange</c>
/// row, the <c>dataset</c> catalogue, <c>dataset_setting</c> and the full <c>segment_dataset</c>
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
              from segment
             order by code
            """, cancellationToken: ct))).ToList();

        var datasets = (await conn.QueryAsync<DatasetDefaults>(new CommandDefinition(
            """
            select code                       as "Code",
                   kind                       as "Kind",
                   default_mode               as "DefaultMode",
                   default_interval_s         as "DefaultIntervalS",
                   default_history_interval_s as "DefaultHistoryIntervalS",
                   default_retention_days     as "DefaultRetentionDays"
              from dataset
            """, cancellationToken: ct)))
            .ToDictionary(c => c.Code, StringComparer.Ordinal);

        var datasetSettings = (await conn.QueryAsync<(string Dataset, string Key, string Value)>(
            new CommandDefinition(
                "select dataset_code, key, value from dataset_setting", cancellationToken: ct)))
            .ToDictionary(r => (r.Dataset, r.Key), r => r.Value);

        var matrix = (await conn.QueryAsync<SegmentDatasetRow>(new CommandDefinition(
            """
            select segment_code       as "SegmentCode",
                   dataset_code       as "DatasetCode",
                   mode               as "Mode",
                   interval_s         as "IntervalS",
                   history_interval_s as "HistoryIntervalS",
                   retention_days     as "RetentionDays",
                   transport          as "Transport"
              from segment_dataset
            """, cancellationToken: ct)))
            .ToDictionary(r => (r.SegmentCode, r.DatasetCode));

        return new SettingsSnapshot(settings, exchanges, datasets, datasetSettings, matrix);
    }
}

/// <summary>
/// One exchange's configuration row — everything that stays on <c>exchange</c> (how to reach it, and
/// whether it runs at all). What and how often to collect moved to <c>segment_dataset</c> (0014).
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

/// <summary>One row of the <c>dataset</c> catalogue — the terminal default of the cascade.</summary>
public sealed record DatasetDefaults
{
    public string Code { get; init; } = "";
    public string Kind { get; init; } = "";
    public string DefaultMode { get; init; } = "";
    public int? DefaultIntervalS { get; init; }

    /// <summary>How often an observation is KEPT, when that differs from how often it is asked for.
    /// null means the dataset does not make that distinction — every pass is written whole — and is
    /// also what tells the console not to offer the field for this dataset (0020).</summary>
    public int? DefaultHistoryIntervalS { get; init; }

    public int? DefaultRetentionDays { get; init; }
}

/// <summary>One cell of the segment×dataset matrix — always present for every pair (0014).</summary>
public sealed record SegmentDatasetRow
{
    public string SegmentCode { get; init; } = "";
    public string DatasetCode { get; init; } = "";
    public string Mode { get; init; } = "";
    public int? IntervalS { get; init; }
    public int? HistoryIntervalS { get; init; }
    public int? RetentionDays { get; init; }
    public string? Transport { get; init; }
}

/// <summary>An immutable read of the whole settings surface: global values, every exchange, the
/// dataset catalogue and the full policy matrix.</summary>
public sealed class SettingsSnapshot
{
    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly IReadOnlyDictionary<string, DatasetDefaults> _datasets;
    private readonly IReadOnlyDictionary<(string Dataset, string Key), string> _datasetSettings;
    private readonly IReadOnlyDictionary<(string Exchange, string Dataset), SegmentDatasetRow> _matrix;

    public SettingsSnapshot(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyList<ExchangeConfig> exchanges,
        IReadOnlyDictionary<string, DatasetDefaults> datasets,
        IReadOnlyDictionary<(string Dataset, string Key), string> datasetSettings,
        IReadOnlyDictionary<(string Exchange, string Dataset), SegmentDatasetRow> matrix)
    {
        _settings = settings;
        Exchanges = exchanges;
        _datasets = datasets;
        _datasetSettings = datasetSettings;
        _matrix = matrix;
    }

    public IReadOnlyList<ExchangeConfig> Exchanges { get; }

    public ExchangeConfig? Exchange(string code) =>
        Exchanges.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.Ordinal));

    public IReadOnlyCollection<DatasetDefaults> Datasets => (IReadOnlyCollection<DatasetDefaults>)_datasets.Values;

    // WS honesty knobs (0010): how fresh a cached WS record must be to be served, and the
    // REST cross-check cadence and drift threshold that catch a silently frozen book. These stay
    // global (0014): they are about the transport, not about any one dataset.
    public TimeSpan WsStaleAfter => TimeSpan.FromSeconds(GetInt("ws_stale_after_s"));
    public TimeSpan WsCrosscheckInterval => TimeSpan.FromSeconds(GetInt("ws_crosscheck_interval_s"));
    public int WsCrosscheckDriftBps => GetInt("ws_crosscheck_drift_bps");

    /// <summary>
    /// The matrix cell for one segment×dataset pair, or null if the pair genuinely has no row —
    /// which should not happen for a real exchange once 0014's backfill has run, but a brand-new
    /// exchange added by hand outside a migration would hit this until its matrix row exists.
    /// </summary>
    public SegmentDatasetRow? Cell(string segmentCode, string datasetCode) =>
        _matrix.GetValueOrDefault((segmentCode, datasetCode));

    /// <summary>The human decision for this pair — 'disabled' / 'on_demand' / 'collect'. Mode has no
    /// cascade: <c>segment_dataset.mode</c> is never null, unlike interval/retention.</summary>
    public string Mode(string segmentCode, string datasetCode) =>
        Cell(segmentCode, datasetCode)?.Mode
        ?? _datasets.GetValueOrDefault(datasetCode)?.DefaultMode
        ?? "disabled";

    /// <summary>Cascade: segment_dataset.interval_s -> dataset.default_interval_s. Throws if
    /// neither level has a value — a dataset whose mode can be 'collect' must always resolve one.</summary>
    public TimeSpan DatasetInterval(string segmentCode, string datasetCode)
    {
        var fromCell = Cell(segmentCode, datasetCode)?.IntervalS;
        var fromDataset = _datasets.GetValueOrDefault(datasetCode)?.DefaultIntervalS;
        var seconds = fromCell ?? fromDataset
            ?? throw new InvalidOperationException(
                $"No interval resolved for {segmentCode}/{datasetCode} — neither the matrix cell nor "
                + "the dataset default is set.");
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Cascade: segment_dataset.history_interval_s -> dataset.default_history_interval_s -> the
    /// poll interval, and never below the poll interval — an observation cannot be kept more often
    /// than it is asked for, so 5 s of keeping against a 10 s poll is 10 s and must be reported as
    /// 10. A dataset with no default does not distinguish asking from keeping: every pass is
    /// written whole, so its history interval simply IS its poll interval (0020).
    /// </summary>
    public TimeSpan HistoryInterval(string segmentCode, string datasetCode)
    {
        var poll = DatasetInterval(segmentCode, datasetCode);
        var configured = Cell(segmentCode, datasetCode)?.HistoryIntervalS
            ?? _datasets.GetValueOrDefault(datasetCode)?.DefaultHistoryIntervalS;
        if (configured is not { } seconds)
        {
            return poll;
        }

        var kept = TimeSpan.FromSeconds(seconds);
        return kept > poll ? kept : poll;
    }

    /// <summary>
    /// Retention for a dataset, dataset-level only — deliberately NOT overridable per segment.
    /// <c>market_snapshot</c> partitions are shared by every exchange; dropping a partition cannot
    /// spare one segment's rows, so a per-segment <c>segment_dataset.retention_days</c> value
    /// is recorded (visible as the operator's intent) but not honoured here. See the 0014 migration
    /// header for the full reasoning. null = never rotated.
    /// </summary>
    public int? DatasetRetentionDays(string datasetCode) =>
        _datasets.GetValueOrDefault(datasetCode)?.DefaultRetentionDays;

    /// <summary>A <c>dataset_setting</c> int value — the knobs that are neither an interval nor a
    /// retention (backfill windows, the delist counter). history_interval_s used to live here and
    /// moved to the cell cascade in 0020; see <see cref="HistoryInterval"/>.</summary>
    public int DatasetSettingInt(string datasetCode, string key) =>
        int.Parse(RequireDatasetSetting(datasetCode, key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    public IReadOnlyList<int> DatasetSettingIntList(string datasetCode, string key) =>
        RequireDatasetSetting(datasetCode, key)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .ToList();

    private string RequireDatasetSetting(string datasetCode, string key) =>
        _datasetSettings.TryGetValue((datasetCode, key), out var value)
            ? value
            : throw new InvalidOperationException($"dataset_setting '{datasetCode}/{key}' is missing from the database.");

    public int GetInt(string key) =>
        int.Parse(Require(key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private string Require(string key) =>
        _settings.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Setting '{key}' is missing from the database.");
}

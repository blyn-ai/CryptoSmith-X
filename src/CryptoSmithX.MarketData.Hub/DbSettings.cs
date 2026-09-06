using System.Globalization;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub;

/// <summary>
/// The Hub's configuration, read from the database rather than appsettings. A plain class like
/// <see cref="Db"/>, not IOptions: settings change at runtime from the admin UI, so a static
/// options object would be a lie. A snapshot of the whole <c>setting</c> table, every venue
/// (<c>exchange</c>) and segment row, the <c>dataset</c> catalogue, <c>dataset_setting</c> and the
/// full <c>segment_dataset</c> matrix is cached for ~30 s; loops read the cached copy on every iteration, so an edit in the UI
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

        // The venue level (0019) and its request budget (0021). One row per exchange, not per
        // segment: the per-IP ceiling is shared by every segment of the venue, which is the whole
        // reason the level exists.
        var venues = (await conn.QueryAsync<VenueConfig>(new CommandDefinition(
            """
            select code                    as "Code",
                   name                    as "Name",
                   request_budget_per_s    as "RequestBudgetPerS",
                   max_concurrent_requests as "MaxConcurrentRequests",
                   request_budget_source   as "RequestBudgetSource"
              from exchange
             order by code
            """, cancellationToken: ct))).ToList();

        var exchanges = (await conn.QueryAsync<ExchangeConfig>(new CommandDefinition(
            """
            select code          as "Code",
                   exchange_code as "ExchangeCode",
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

        return new SettingsSnapshot(settings, venues, exchanges, datasets, datasetSettings, matrix);
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

    /// <summary>The venue this trading surface belongs to (0019). Everything that is shared between
    /// two segments of one exchange — today the request budget — is keyed on this, not on
    /// <see cref="Code"/>.</summary>
    public string ExchangeCode { get; init; } = "";

    public string Adapter { get; init; } = "";
    public string? BaseUrl { get; init; }
    public string? ChartsUrl { get; init; }
    public string? WsUrl { get; init; }
    public string[] QuoteAssets { get; init; } = [];
    public string[] Blacklist { get; init; } = [];
    public string Status { get; init; } = "";
}

/// <summary>
/// One venue — an exchange as an organisation, and the only level at which a request budget means
/// anything: the per-IP ceiling is shared by every segment underneath it (0019, 0021). This is the
/// one <c>VenueConfig</c>; the depth/WS blueprints each sketched their own and they disagreed.
/// </summary>
public sealed record VenueConfig
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Requests per second we allow ourselves against this venue, all segments together.</summary>
    public int RequestBudgetPerS { get; init; }

    /// <summary>How many of those may be in flight at once. Not a budget — a latency knob; see the
    /// 0021 header.</summary>
    public int MaxConcurrentRequests { get; init; }

    /// <summary>'documented' | 'measured' | 'assumed'. Carried into the process so an operator asking
    /// "where did 20 req/s come from?" gets the honest answer rather than the comfortable one.</summary>
    public string RequestBudgetSource { get; init; } = "";
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
        IReadOnlyList<VenueConfig> venues,
        IReadOnlyList<ExchangeConfig> exchanges,
        IReadOnlyDictionary<string, DatasetDefaults> datasets,
        IReadOnlyDictionary<(string Dataset, string Key), string> datasetSettings,
        IReadOnlyDictionary<(string Exchange, string Dataset), SegmentDatasetRow> matrix)
    {
        _settings = settings;
        Venues = venues;
        Exchanges = exchanges;
        _datasets = datasets;
        _datasetSettings = datasetSettings;
        _matrix = matrix;
    }

    /// <summary>The <c>exchange</c> rows — venues. <see cref="Exchanges"/> is the older name for what
    /// 0019 renamed to segments; the two levels are not the same thing and only this one has a budget.</summary>
    public IReadOnlyList<VenueConfig> Venues { get; }

    public VenueConfig? Venue(string code) =>
        Venues.FirstOrDefault(v => string.Equals(v.Code, code, StringComparison.Ordinal));

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

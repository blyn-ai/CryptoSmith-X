namespace CryptoSmithX.WebApp.Models;

// timestamptz comes back from Npgsql as DateTime with Kind=Utc; that is what these say.

public sealed record TenantRow(string Code, string Name, DateTime CreatedAt);

public sealed record BotListItem(
    int Id,
    string TenantCode,
    string BotInstanceId,
    string Name,
    bool IsEnabled,
    DateTime? LastHeartbeatAt,
    double? HeartbeatAgeSeconds,
    bool HasToken);

public sealed record BotEventRow(
    string EventId,
    DateTime Utc,
    string Type,
    string Payload,
    DateTime ReceivedAt);

public sealed record BotDetails(
    int Id,
    string TenantCode,
    string BotInstanceId,
    string Name,
    bool IsEnabled,
    DateTime? LastHeartbeatAt,
    string? LastHeartbeatJson,
    int PolicyVersion,
    string? PolicyJson,
    IReadOnlyList<BotEventRow> Events);

/// <summary>The one-time token banner shown right after a bot is created or its token regenerated.</summary>
public sealed record NewTokenNotice(string BotInstanceId, string Token);

public sealed record ExchangeListItem(
    string Code,
    string Name,
    string Status,
    string? Description,
    int TradingInstruments,
    int KnownInstruments,
    int? MaxFailures,
    double? AvgDurationMs,
    double? DiscoveryAgeSeconds);

public sealed record ExchangeCollectorRow(
    string Collector,
    double? LastSuccessAgeSeconds,
    int ConsecutiveFailures,
    int? InstrumentsExpected,
    int? LastDurationMs,
    double? AvgDurationMs,
    string? LastError,
    double? LastErrorAgeSeconds);

public sealed record StaleInstrument(int Id, string Symbol, double? AgeSeconds);

/// <summary>
/// The editable configuration of one exchange. Interval overrides are null when the global wins.
/// Property-init so Dapper materialises the text[] columns through setters, not the constructor.
/// </summary>
public sealed record ExchangeConfigRow
{
    public string Adapter { get; init; } = "";
    public string? BaseUrl { get; init; }
    public string? ChartsUrl { get; init; }
    public string? WsUrl { get; init; }
    public string[] QuoteAssets { get; init; } = [];
    public string[] Blacklist { get; init; } = [];
    public int? SnapshotIntervalS { get; init; }
    public int? CandleIntervalS { get; init; }
    public int? DiscoveryIntervalMin { get; init; }
    public int? FundingIntervalMin { get; init; }
    public int? DepthIntervalS { get; init; }
    public string? UpdatedBy { get; init; }
}

/// <summary>The editable configuration of an exchange (everything except status, which is guarded).</summary>
public sealed record ExchangeSaveInput(
    string Code,
    string Name,
    string? Description,
    string? BaseUrl,
    string? ChartsUrl,
    string? WsUrl,
    string[] QuoteAssets,
    string[] Blacklist,
    int? SnapshotIntervalS,
    int? CandleIntervalS,
    int? DiscoveryIntervalMin,
    int? FundingIntervalMin,
    int? DepthIntervalS,
    string? UpdatedBy);

/// <summary>One global market-data setting, for the System → Settings page.</summary>
public sealed record SettingRow(
    string Key, string Value, string Kind, string Description, DateTime UpdatedAt, string? UpdatedBy);

public sealed record ExchangeDetails(
    ExchangeListItem Exchange,
    ExchangeConfigRow Config,
    IReadOnlyDictionary<string, int> GlobalIntervals,
    IReadOnlyList<ExchangeCollectorRow> Collectors,
    IReadOnlyList<StaleInstrument> Stalest,
    IReadOnlyList<double> Throughput,
    IReadOnlyList<LatencySeries> Latency,
    IReadOnlyList<FeedRow> Feeds,
    IReadOnlyList<FeedDetails> FeedDialogs,
    IReadOnlyList<CollectionGapRow> Gaps);

/// <summary>
/// An interval this venue was not observed for. The point of showing these is that a hole in the
/// data and a quiet market look identical on every chart in this console; only this row says which
/// one it was.
/// </summary>
public sealed record CollectionGapRow(
    string Collector,
    DateTime GapStart,
    DateTime? GapEnd,
    string Cause,
    string? Detail,
    double? SecondsLong);

// ── Data feeds (collections, 0014 phase 2) ──────────────────────────────────

/// <summary>One row of the "Data feeds" panel: one collection for one exchange, all three axes —
/// capability (fact), policy (decided) and health (observed) — kept visually and structurally
/// separate, per plans/design/data-feeds/HANDOFF.md.</summary>
public sealed record FeedRow(
    string CollectionCode,
    string CollectionName,
    string Kind,
    short SortOrder,
    bool? VenueSupports,
    bool? WeImplement,
    string? HistoryDepth,
    string? HistorySource,
    string Mode,
    string? Transport,
    int? EffectiveIntervalS,
    int? EffectiveRetentionDays,
    string? Note,
    double? LastSuccessAgeSeconds,
    int ConsecutiveFailures,
    int? LastDurationMs,
    double? AvgDurationMs);

/// <summary>One capability_key row in the Edit feed dialog's read-only left column — a value with
/// no source is an opinion, so <see cref="Source"/>/<see cref="FilledBy"/>/<see cref="FilledAt"/> are
/// shown with equal weight to the value itself.</summary>
public sealed record FeedCapabilityRow(
    string Key, string Label, string Kind, bool LossRelevant,
    string? Value, string? Source, string? FilledBy, DateTime? FilledAt);

/// <summary>Everything the Edit feed dialog needs for one collection: the read-only capability
/// column and the editable policy column, including the own/collection/global cascade for interval
/// and retention.</summary>
public sealed record FeedDetails(
    string CollectionCode,
    string CollectionName,
    string CollectionDescription,
    string Kind,
    IReadOnlyList<FeedCapabilityRow> Capabilities,
    string Mode,
    bool WeImplement,
    int? OwnIntervalS,
    int? CollectionDefaultIntervalS,
    int? OwnRetentionDays,
    int? CollectionDefaultRetentionDays,
    string? Transport,
    IReadOnlyList<string> TransportOptions,
    string? Note,
    string? UpdatedBy,
    DateTime? UpdatedAt,
    string? LatestCapabilityLogLine);

/// <summary>The editable policy of one exchange×collection cell.</summary>
public sealed record FeedSaveInput(
    string ExchangeCode,
    string CollectionCode,
    string Mode,
    int? IntervalS,
    int? RetentionDays,
    string? Transport,
    string? Note,
    string? ConfirmCode,
    string? UpdatedBy);

// ── Collections catalogue (screen 3) ────────────────────────────────────────

public sealed record CollectionCard(
    string Code, string Name, string Description, string Kind, short SortOrder,
    string DefaultMode, int? DefaultIntervalS, int? DefaultRetentionDays,
    IReadOnlyList<CollectionVenueRow> Venues);

public sealed record CollectionVenueRow(string CollectionCode, string ExchangeCode, string ExchangeName, string Mode, string? Note);

// ── Assets ────────────────────────────────────────────────────────────────
public sealed record AssetListItem(
    string Code,
    string? Name,
    int ListingCount,
    string? ListingsSummary,          // "kraken-futures 1 · fake 1"
    double? OpenInterestNotional,     // sum over listings of open_interest * mark
    double? WorstSnapshotAgeSeconds); // oldest snapshot among the listings

/// <summary>One listing of an asset on one exchange — the row that makes the Asset page a comparison.</summary>
public sealed record AssetListing(
    int InstrumentId,
    string ExchangeCode,
    string Symbol,
    string Status,
    bool Collect,
    double? LastPrice,
    double? FundingRate,
    double? OpenInterestNotional,
    double? SpreadBps,
    double? Depth25Notional,          // bid + ask within 25 bps
    double? SnapshotAgeSeconds);

public sealed record AssetAliasRow(string? ExchangeCode, string Alias, string AssetCode, decimal Multiplier, string? Note);

public sealed record AssetDetails(
    string Code,
    string? Name,
    string? Note,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    IReadOnlyList<AssetListing> Listings,
    IReadOnlyList<AssetAliasRow> Aliases);

// ── Instruments ───────────────────────────────────────────────────────────
public sealed record InstrumentListItem(
    int Id,
    string ExchangeCode,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Status,
    bool Collect,
    double? LastPrice,
    double? FundingRate,
    double? OpenInterestNotional,
    double? SnapshotAgeSeconds);

/// <summary>One page of the instrument list plus the filter/sort state, so the view can build links.</summary>
public sealed record InstrumentPage(
    IReadOnlyList<InstrumentListItem> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> Exchanges,
    string? Exchange,
    string? Status,
    bool OnlyTrading,
    string? Search,
    string Sort)
{
    public int From => Total == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int To => Math.Min(Page * PageSize, Total);
    public int Pages => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>The current market snapshot for one instrument, with a separate age per data layer.</summary>
public sealed record SnapshotView(
    DateTime ReceivedAt,
    double? SnapshotAgeSeconds,
    double LastPrice,
    double BidPrice,
    double AskPrice,
    double BidSize,
    double AskSize,
    double? SpreadBps,
    double MarkPrice,
    double IndexPrice,
    double FundingRate,
    double Turnover24h,
    double OpenInterest,
    double OpenInterestNotional,
    double? DepthBid10,
    double? DepthAsk10,
    double? DepthBid25,
    double? DepthAsk25,
    double? DepthBid50,
    double? DepthAsk50,
    DateTime? DepthAt,
    double? DepthAgeSeconds);

public sealed record CandlePoint(DateTime OpenTime, double Open, double High, double Low, double Close);

public sealed record MetricPoint(DateTime HourTime, double OpenInterestLast, double FundingRateLast, double? SpreadBpsAvg);

public sealed record FundingRow(DateTime FundingTime, double Rate);

/// <summary>Data-coverage summary for the detail page: 1-minute completeness and the stored ranges.</summary>
public sealed record CoverageView(
    int Minutes24h,
    int Holes24h,
    DateTime? CandleFrom,
    DateTime? CandleTo,
    DateTime? FundingFrom,
    DateTime? FundingTo,
    double? LastCandleAgeSeconds,
    double? LastFundingAgeSeconds,
    bool ExchangeCollecting,
    bool Silent);

public sealed record InstrumentDetails(
    int Id,
    string ExchangeCode,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Status,
    DateTime StatusChangedAt,
    DateTime? ListedAt,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    bool Collect,
    string? CollectNote,
    DateTime? CollectChangedAt,
    string? CollectChangedBy,
    short FundingIntervalHours,
    SnapshotView? Snapshot,
    int Timeframe,
    IReadOnlyList<int> Timeframes,
    IReadOnlyList<CandlePoint> Candles,
    IReadOnlyList<MetricPoint> Metrics,
    IReadOnlyList<FundingRow> Funding,
    CoverageView Coverage,
    IReadOnlyList<SiblingListing> Siblings);

/// <summary>Another venue's listing of the same canonical asset — the hop between exchanges.</summary>
public sealed record SiblingListing(int Id, string ExchangeCode, string Symbol);

// ── Admin dashboard ───────────────────────────────────────────────────────
public sealed record DashExchange(
    string Code, string Name, string Status, string Health,
    int TradingInstruments, int KnownInstruments, double? WorstAgeSeconds,
    IReadOnlyList<double> Spark);

public sealed record DashCollector(
    string ExchangeCode, string Collector, double? LastSuccessAgeSeconds,
    int ConsecutiveFailures, int? AvgDurationMs, string? LastError, string Health);

public sealed record DashBot(
    int Id,
    string TenantCode, string BotInstanceId, double? LastHeartbeatAgeSeconds, bool Online);

public sealed record DashEvent(DateTime Utc, string Type, string TenantCode, int BotId, string BotInstanceId, bool IsError);

public sealed record DashTenant(string Code, int BotCount, DateTime CreatedAt);

public sealed record Dashboard(
    int ExchangesEnabled, int ExchangesTotal, int ExchangesMaintenance, int ExchangesPlanned,
    int CollectorsOk, int CollectorsTotal, int CollectorsFailing,
    int InstrumentsTrading, int InstrumentsKnown,
    int BotsOnline, int BotsTotal, string? SilentBotNote,
    int EventsLastHour,
    int Failing, int Degraded, string Verdict,
    IReadOnlyList<DashExchange> Exchanges,
    IReadOnlyList<DashCollector> Collectors,
    IReadOnlyList<DashBot> Bots,
    IReadOnlyList<DashEvent> Events,
    IReadOnlyList<DashTenant> Tenants,
    IReadOnlyList<double> IngestBuckets, int IngestPeak,
    DateTime AsOf);

// ── Clients (derived from tenant + bot; no client table yet) ───────────────
public sealed record ClientListItem(
    string Code, string Name, int BotCount, bool Online, double? HeartbeatAgeSeconds, int Events24h);

public sealed record ClientBot(int Id, string BotInstanceId, bool Online, double? HeartbeatAgeSeconds);

public sealed record ClientDetails(
    string Code, string Name, bool Online, double? HeartbeatAgeSeconds,
    int BotsOnline, int BotsTotal, int Events24h,
    IReadOnlyList<ClientBot> Bots);

/// <summary>One row of the header search — kind decides the group, url is the landing page.</summary>
public sealed record SearchHit(string Kind, string Title, string Note, string Url);

// ── collector runs (0009): история прогонов и «что пришло» ────────────────
public sealed record CollectorRunRow(
    long Id, string Collector, DateTime StartedAt, int DurationMs, bool Ok, string? Error, int? Items);

/// <summary>One latency series per collector for the exchange page trend.</summary>
public sealed record LatencySeries(string Collector, IReadOnlyList<double> AvgMs);

public sealed record RunDataRow(string Symbol, string What, DateTime When);

/// <summary>A run plus the data whose timestamps fall inside its window (time-based, not run-id based).</summary>
public sealed record RunDetails(
    string ExchangeCode, CollectorRunRow Run,
    string Caption, int Total, IReadOnlyList<RunDataRow> Rows, string EmptyNote);

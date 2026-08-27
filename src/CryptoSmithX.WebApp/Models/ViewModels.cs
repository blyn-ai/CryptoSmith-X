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

/// <summary>The market-data console, read straight from the marketdata tables (queries copied from the Api).</summary>
public sealed record MarketDataConsole(
    IReadOnlyList<dynamic> Collectors,
    IReadOnlyList<dynamic> Stale,
    IReadOnlyList<dynamic> Instruments,
    IReadOnlyList<dynamic> Snapshot,
    DateTimeOffset AsOf);

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

public sealed record StaleInstrument(string Symbol, double? AgeSeconds);

public sealed record ExchangeDetails(
    ExchangeListItem Exchange,
    IReadOnlyList<ExchangeCollectorRow> Collectors,
    IReadOnlyList<StaleInstrument> Stalest,
    IReadOnlyList<double> Throughput);

// ── Admin dashboard ───────────────────────────────────────────────────────
public sealed record DashExchange(
    string Code, string Name, string Status, string Health,
    int TradingInstruments, int KnownInstruments, double? WorstAgeSeconds,
    IReadOnlyList<double> Spark);

public sealed record DashCollector(
    string ExchangeCode, string Collector, double? LastSuccessAgeSeconds,
    int ConsecutiveFailures, int? AvgDurationMs, string? LastError, string Health);

public sealed record DashBot(
    string TenantCode, string BotInstanceId, double? LastHeartbeatAgeSeconds, bool Online);

public sealed record DashEvent(DateTime Utc, string Type, string TenantCode, string BotInstanceId, bool IsError);

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

public sealed record ClientBot(string BotInstanceId, bool Online, double? HeartbeatAgeSeconds);

public sealed record ClientDetails(
    string Code, string Name, bool Online, double? HeartbeatAgeSeconds,
    int BotsOnline, int BotsTotal, int Events24h,
    IReadOnlyList<ClientBot> Bots);

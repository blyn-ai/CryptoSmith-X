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

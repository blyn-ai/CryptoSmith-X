using System.Data.Common;
using Dapper;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// Kraken credentials assigned to one bot instance. Secrets are only read server-side when the
/// owner explicitly verifies the saved pair; they are never rendered, logged, or returned by HTTP.
/// </summary>
public static class KrakenCredentialStore
{
    public const string FuturesScope = "kraken_futures";
    public const string SpotScope = "kraken_spot";

    public static async Task<KrakenCredentialSummary> LoadSummaryAsync(
        DbConnection connection,
        string botInstanceId,
        string scope,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CredentialRow>(new CommandDefinition(
            """
            select api_key as "ApiKey",
                   updated_at as "UpdatedAt"
              from public.bot_instance_api_credentials
             where bot_instance_id = @botInstanceId
               and api_scope = @scope
            """,
            new { botInstanceId, scope },
            cancellationToken: cancellationToken));

        if (row is null)
        {
            return KrakenCredentialSummary.NotConfigured;
        }

        var updatedAt = DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc);
        return new KrakenCredentialSummary(
            true,
            MaskApiKey(row.ApiKey),
            new DateTimeOffset(updatedAt));
    }

    public static Task<StoredKrakenCredentials?> LoadAsync(
        DbConnection connection,
        string botInstanceId,
        string scope,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<StoredKrakenCredentials>(new CommandDefinition(
            """
            select api_key as "ApiKey",
                   api_secret as "ApiSecret"
              from public.bot_instance_api_credentials
             where bot_instance_id = @botInstanceId
               and api_scope = @scope
            """,
            new { botInstanceId, scope },
            cancellationToken: cancellationToken));

    public static Task SaveAsync(
        DbConnection connection,
        string botInstanceId,
        string scope,
        string apiKey,
        string apiSecret,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            insert into public.bot_instance_api_credentials
                (bot_instance_id, api_scope, api_key, api_secret)
            values
                (@botInstanceId, @scope, @apiKey, @apiSecret)
            on conflict (bot_instance_id, api_scope)
            do update set
                api_key = excluded.api_key,
                api_secret = excluded.api_secret,
                updated_at = now()
            """,
            new { botInstanceId, scope, apiKey, apiSecret },
            cancellationToken: cancellationToken));

    public static Task RemoveAsync(
        DbConnection connection,
        string botInstanceId,
        string scope,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            delete from public.bot_instance_api_credentials
             where bot_instance_id = @botInstanceId
               and api_scope = @scope
            """,
            new { botInstanceId, scope },
            cancellationToken: cancellationToken));

    public static bool IsSupportedScope(string? scope) =>
        scope is FuturesScope or SpotScope;

    private static string MaskApiKey(string apiKey) =>
        apiKey.Length <= 8 ? apiKey : $"{apiKey[..4]}…{apiKey[^4..]}";

    private sealed record CredentialRow(string ApiKey, DateTime UpdatedAt);
}

public sealed record KrakenCredentialSummary(
    bool Configured,
    string? ApiKeyHint,
    DateTimeOffset? UpdatedAt)
{
    public static KrakenCredentialSummary NotConfigured { get; } = new(false, null, null);
}

public sealed record StoredKrakenCredentials(string ApiKey, string ApiSecret);

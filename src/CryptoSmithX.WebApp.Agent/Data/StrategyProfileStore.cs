using System.Data.Common;
using System.Text.Json.Nodes;
using Dapper;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// Reads the strategy profile assigned to a bot instance. The futures worker
/// reads the same three tables, so this is a view of the worker's real input,
/// not a configuration mirror maintained by the Agent.
/// </summary>
public static class StrategyProfileStore
{
    internal const string LoadActiveSql =
        """
        select assignment.profile_id as "ProfileId",
               profile.name as "ProfileName",
               assignment.profile_revision as "Revision",
               revision.values_jsonb::text as "ValuesJson",
               assignment.overrides_jsonb::text as "OverridesJson",
               revision.change_note as "ChangeNote",
               revision.created_at as "CreatedAt"
          from bot_instance_strategy_profiles assignment
          join bot_strategy_profiles profile
            on profile.profile_id = assignment.profile_id
          join bot_strategy_profile_revisions revision
            on revision.profile_id = assignment.profile_id
           and revision.revision = assignment.profile_revision
         where assignment.bot_instance_id = @botInstanceId
           and assignment.is_active = true
           and profile.is_archived = false
        """;

    internal const string LoadHistorySql =
        """
        select revision as "Revision",
               change_note as "ChangeNote",
               created_at as "CreatedAt"
          from bot_strategy_profile_revisions
         where profile_id = @profileId
         order by revision desc
         limit @limit
        """;

    public static async Task<ActiveStrategyProfile?> LoadActiveAsync(
        DbConnection connection,
        string botInstanceId,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ActiveStrategyProfileRow>(new CommandDefinition(
            LoadActiveSql,
            new { botInstanceId },
            cancellationToken: cancellationToken));

        return row is null
            ? null
            : new ActiveStrategyProfile(
                row.ProfileId,
                row.ProfileName,
                row.Revision,
                ParseObject(row.ValuesJson, "values_jsonb"),
                ParseObject(row.OverridesJson, "overrides_jsonb"),
                row.ChangeNote,
                DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    public static async Task<IReadOnlyList<StrategyProfileRevision>> LoadHistoryAsync(
        DbConnection connection,
        Guid profileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<StrategyProfileRevision>(new CommandDefinition(
            LoadHistorySql,
            new { profileId, limit = Math.Clamp(limit, 1, 100) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public static JsonObject ResolveValues(ActiveStrategyProfile profile)
    {
        var result = profile.Values.DeepClone().AsObject();
        Merge(result, profile.Overrides);
        return result;
    }

    private static JsonObject ParseObject(string json, string source) =>
        JsonNode.Parse(json)?.AsObject()
        ?? throw new InvalidOperationException($"Strategy profile {source} must be a JSON object.");

    private static void Merge(JsonObject destination, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonObject sourceObject
                && destination[property.Key] is JsonObject destinationObject)
            {
                Merge(destinationObject, sourceObject);
                continue;
            }

            destination[property.Key] = property.Value?.DeepClone();
        }
    }

    public sealed record ActiveStrategyProfileRow(
        Guid ProfileId,
        string ProfileName,
        int Revision,
        string ValuesJson,
        string OverridesJson,
        string? ChangeNote,
        DateTime CreatedAt);
}

public sealed record ActiveStrategyProfile(
    Guid ProfileId,
    string ProfileName,
    int Revision,
    JsonObject Values,
    JsonObject Overrides,
    string? ChangeNote,
    DateTime CreatedAt);

public sealed record StrategyProfileRevision(
    int Revision,
    string? ChangeNote,
    DateTime CreatedAt);

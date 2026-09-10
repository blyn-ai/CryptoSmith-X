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

    internal const string LockActiveAssignmentSql =
        """
        select profile_id as "ProfileId",
               profile_revision as "Revision"
          from bot_instance_strategy_profiles
         where bot_instance_id = @botInstanceId
           and is_active = true
         for update
        """;

    internal const string LockProfileSql =
        """
        select profile_id
          from bot_strategy_profiles
         where profile_id = @profileId
         for update
        """;

    internal const string NextRevisionSql =
        """
        select coalesce(max(revision), 0) + 1
          from bot_strategy_profile_revisions
         where profile_id = @profileId
        """;

    internal const string InsertRevisionSql =
        """
        insert into bot_strategy_profile_revisions
            (profile_id, revision, values_jsonb, change_note, created_by)
        values
            (@profileId, @revision, cast(@valuesJson as jsonb), @changeNote, @changedBy)
        """;

    internal const string ActivateRevisionSql =
        """
        update bot_instance_strategy_profiles
           set profile_revision = @revision,
               overrides_jsonb = '{}'::jsonb,
               updated_at = now(),
               updated_by = @changedBy
         where bot_instance_id = @botInstanceId
           and profile_id = @profileId
           and profile_revision = @expectedRevision
           and is_active = true
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

    /// <summary>
    /// Creates the next complete profile revision in memory. Instance overrides
    /// are intentionally folded into it: the new revision is the exact strategy
    /// the owner reviewed, and its assignment starts without a hidden second
    /// layer that could mask a saved field.
    /// </summary>
    public static JsonObject BuildNextRevisionValues(
        ActiveStrategyProfile active,
        IReadOnlyDictionary<string, decimal> parameters)
    {
        var values = ResolveValues(active);
        foreach (var parameter in parameters)
        {
            StrategyParameterCatalog.Write(StrategyParameterCatalog.Get(parameter.Key), values, parameter.Value);
        }

        return values;
    }

    /// <summary>
    /// Atomically advances a bot's active strategy revision and its four runtime
    /// limits. The expected profile revision prevents a stale browser form from
    /// silently replacing another save made after the page was opened.
    /// </summary>
    public static async Task<ActiveStrategyProfile> SaveAsync(
        DbConnection connection,
        string botInstanceId,
        ActiveStrategyProfile expected,
        StrategyProfileSave save,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var assignment = await connection.QuerySingleOrDefaultAsync<ActiveAssignmentRow>(new CommandDefinition(
            LockActiveAssignmentSql,
            new { botInstanceId },
            transaction,
            cancellationToken: cancellationToken));
        if (assignment is null)
        {
            throw new InvalidOperationException($"Bot instance '{botInstanceId}' has no active strategy assignment.");
        }

        if (assignment.ProfileId != expected.ProfileId || assignment.Revision != expected.Revision)
        {
            throw new StrategyProfileConflictException(botInstanceId, expected.Revision, assignment.Revision);
        }

        await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            LockProfileSql,
            new { profileId = expected.ProfileId },
            transaction,
            cancellationToken: cancellationToken));
        var revision = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            NextRevisionSql,
            new { profileId = expected.ProfileId },
            transaction,
            cancellationToken: cancellationToken));
        var values = BuildNextRevisionValues(expected, save.Parameters);
        var changeNote = NormalizeNote(save.ChangeNote);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertRevisionSql,
            new
            {
                profileId = expected.ProfileId,
                revision,
                valuesJson = values.ToJsonString(),
                changeNote,
                changedBy = save.ChangedBy,
            },
            transaction,
            cancellationToken: cancellationToken));
        var activated = await connection.ExecuteAsync(new CommandDefinition(
            ActivateRevisionSql,
            new
            {
                botInstanceId,
                profileId = expected.ProfileId,
                revision,
                expectedRevision = expected.Revision,
                changedBy = save.ChangedBy,
            },
            transaction,
            cancellationToken: cancellationToken));
        if (activated != 1)
        {
            throw new StrategyProfileConflictException(botInstanceId, expected.Revision, null);
        }

        await TradeProfileStore.SaveInTransactionAsync(
            connection,
            transaction,
            botInstanceId,
            save.RuntimeLimits,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ActiveStrategyProfile(
            expected.ProfileId,
            expected.ProfileName,
            revision,
            values,
            new JsonObject(),
            changeNote,
            DateTime.UtcNow);
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

    private static string? NormalizeNote(string? note)
    {
        var normalized = note?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Length <= 1_000
                ? normalized
                : throw new ArgumentOutOfRangeException(nameof(note), "A strategy note cannot exceed 1000 characters.");
    }

    public sealed record ActiveStrategyProfileRow(
        Guid ProfileId,
        string ProfileName,
        int Revision,
        string ValuesJson,
        string OverridesJson,
        string? ChangeNote,
        DateTime CreatedAt);

    public sealed record ActiveAssignmentRow(Guid ProfileId, int Revision);
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

public sealed record StrategyProfileSave(
    TradeProfile RuntimeLimits,
    IReadOnlyDictionary<string, decimal> Parameters,
    string? ChangeNote,
    string ChangedBy);

public sealed class StrategyProfileConflictException(
    string botInstanceId,
    int expectedRevision,
    int? actualRevision) : Exception(
        actualRevision is null
            ? $"The active strategy assignment for '{botInstanceId}' changed before this save completed."
            : $"The active strategy revision for '{botInstanceId}' changed from {expectedRevision} to {actualRevision}.")
{
}

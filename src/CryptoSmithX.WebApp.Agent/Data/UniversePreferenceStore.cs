using System.Data.Common;
using Dapper;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// One bot's deviations from the shared futures universe: how many pairs the worker picks by
/// itself, which pairs it always adds, and which it never opens. The universe itself — every
/// enabled futures pair in <c>instrument_registry</c> — is shared by all bots and is only read
/// here, never copied per bot.
///
/// The futures worker reads the same row every cycle, after the strategy profile, so
/// <c>auto_instrument_count</c> replaces the profile's <c>Trading.MaxActiveInstruments</c>
/// whenever the row exists.
/// </summary>
public static class UniversePreferenceStore
{
    internal const string LoadSql =
        """
        select auto_instrument_count as "AutoInstrumentCount",
               force_include_pairs as "ForceIncludePairs",
               force_exclude_pairs as "ForceExcludePairs",
               updated_at as "UpdatedAt",
               updated_by as "UpdatedBy"
          from bot_universe_preferences
         where bot_instance_id = @botInstanceId
        """;

    internal const string FuturesPairsSql =
        """
        select pair
          from instrument_registry
         where venue = 'futures'
           and enabled = true
         order by pair
        """;

    /// <summary>
    /// An UPDATE, never an insert. A bot without a row runs the lists from its own deployment
    /// config, which this page cannot see; creating a row would silently replace them with
    /// whatever the form happened to hold. The expected <c>updated_at</c> is the optimistic check:
    /// a form opened before another write lands no rows instead of overwriting it.
    /// </summary>
    internal const string UpdateSql =
        """
        update bot_universe_preferences
           set auto_instrument_count = @autoInstrumentCount,
               force_include_pairs = @forceIncludePairs,
               force_exclude_pairs = @forceExcludePairs,
               updated_at = now(),
               updated_by = @updatedBy
         where bot_instance_id = @botInstanceId
           and updated_at = @expectedUpdatedAt
        """;

    public static async Task<UniversePreferences?> LoadAsync(
        DbConnection connection,
        BotInstanceOwner owner,
        CancellationToken cancellationToken)
    {
        // Into a settable row, not the record: Npgsql reports a text[] column's type as
        // System.Array, so Dapper finds no constructor matching string[] and refuses the record.
        var row = await connection.QuerySingleOrDefaultAsync<UniversePreferencesRow>(new CommandDefinition(
            LoadSql,
            new { botInstanceId = owner.BotInstanceId },
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new UniversePreferences(
                row.AutoInstrumentCount,
                row.ForceIncludePairs,
                row.ForceExcludePairs,
                DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc),
                row.UpdatedBy);
    }

    public static async Task<IReadOnlySet<string>> LoadFuturesPairsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var pairs = await connection.QueryAsync<string>(new CommandDefinition(
            FuturesPairsSql,
            cancellationToken: cancellationToken));
        return pairs.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes the owner's row. The bot is the one the signed-in account resolved to; the browser
    /// never names it. Returns false when the row changed since <paramref name="expectedUpdatedAt"/>
    /// or does not exist.
    /// </summary>
    public static async Task<bool> SaveAsync(
        DbConnection connection,
        BotInstanceOwner owner,
        UniverseSelection selection,
        DateTime expectedUpdatedAt,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            UpdateSql,
            new
            {
                botInstanceId = owner.BotInstanceId,
                autoInstrumentCount = selection.AutoInstrumentCount,
                forceIncludePairs = selection.ForceIncludePairs.ToArray(),
                forceExcludePairs = selection.ForceExcludePairs.ToArray(),
                updatedBy,
                expectedUpdatedAt = DateTime.SpecifyKind(expectedUpdatedAt, DateTimeKind.Utc),
            },
            cancellationToken: cancellationToken));
        return updated == 1;
    }
}

internal sealed class UniversePreferencesRow
{
    public int AutoInstrumentCount { get; init; }

    public string[] ForceIncludePairs { get; init; } = [];

    public string[] ForceExcludePairs { get; init; } = [];

    public DateTime UpdatedAt { get; init; }

    public string? UpdatedBy { get; init; }
}

public sealed record UniversePreferences(
    int AutoInstrumentCount,
    string[] ForceIncludePairs,
    string[] ForceExcludePairs,
    DateTime UpdatedAt,
    string? UpdatedBy);

/// <summary>A validated universe choice, ready to store: pairs normalised and known to the registry.</summary>
public sealed record UniverseSelection(
    int AutoInstrumentCount,
    IReadOnlyList<string> ForceIncludePairs,
    IReadOnlyList<string> ForceExcludePairs);

/// <summary>
/// The pair lists as the reader types them and as the worker reads them. Pure, so the rules
/// the form enforces are the rules the tests hold.
/// </summary>
public static class UniversePairList
{
    public const int MinimumAutoInstrumentCount = 0;

    public const int MaximumAutoInstrumentCount = 500;

    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    /// <summary>
    /// Comma, semicolon or newline separated; trimmed, upper-cased, duplicates dropped. The first
    /// occurrence keeps its place, so the stored list reads in the order it was typed. The worker
    /// applies the same normalisation when it loads the row.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        (text ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>How a stored list goes back into the text box.</summary>
    public static string Format(IEnumerable<string> pairs) => string.Join(", ", pairs);

    /// <summary>
    /// The form's answer: a selection to store, or the field-keyed messages that refuse it. Every
    /// pair must be an enabled futures pair of the shared registry, and no pair may be both
    /// always-included and excluded — the worker refuses such a row outright.
    /// </summary>
    public static UniverseValidation Validate(
        int? autoInstrumentCount,
        string? forceIncludeText,
        string? forceExcludeText,
        IReadOnlySet<string> futuresPairs)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (autoInstrumentCount is not { } count
            || count < MinimumAutoInstrumentCount
            || count > MaximumAutoInstrumentCount)
        {
            errors[UniverseFields.AutoInstrumentCount] =
                $"Įvesk sveiką skaičių nuo {MinimumAutoInstrumentCount} iki {MaximumAutoInstrumentCount}";
        }

        var include = Parse(forceIncludeText);
        var exclude = Parse(forceExcludeText);
        CheckKnown(include, futuresPairs, UniverseFields.ForceIncludePairs, errors);
        CheckKnown(exclude, futuresPairs, UniverseFields.ForceExcludePairs, errors);

        var overlap = include.Intersect(exclude, StringComparer.Ordinal).ToList();
        if (overlap.Count > 0)
        {
            errors[UniverseFields.ForceExcludePairs] =
                $"Pora negali būti abiejuose sąrašuose: {Sample(overlap)}";
        }

        return errors.Count > 0
            ? new UniverseValidation(null, errors)
            : new UniverseValidation(new UniverseSelection(autoInstrumentCount!.Value, include, exclude), errors);
    }

    private static void CheckKnown(
        IReadOnlyList<string> pairs,
        IReadOnlySet<string> futuresPairs,
        string field,
        Dictionary<string, string> errors)
    {
        var unknown = pairs.Where(pair => !futuresPairs.Contains(pair)).ToList();
        if (unknown.Count > 0)
        {
            errors[field] = $"Tokių futures porų registre nėra: {Sample(unknown)}";
        }
    }

    private static string Sample(IReadOnlyList<string> pairs) =>
        pairs.Count <= 8
            ? string.Join(", ", pairs)
            : string.Join(", ", pairs.Take(8)) + $" ir dar {pairs.Count - 8}";
}

public sealed record UniverseValidation(
    UniverseSelection? Selection,
    IReadOnlyDictionary<string, string> Errors);

/// <summary>The form field names, shared by the view, the binder and the error keys.</summary>
public static class UniverseFields
{
    public const string AutoInstrumentCount = "autoInstrumentCount";

    public const string ForceIncludePairs = "forceIncludePairs";

    public const string ForceExcludePairs = "forceExcludePairs";
}

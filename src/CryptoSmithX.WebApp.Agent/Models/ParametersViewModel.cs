using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>What the parameters screen renders. Four numbers, where each came from, and whatever
/// the last attempt to save them had to say.</summary>
public sealed class ParametersViewModel
{
    public required string BotInstanceId { get; init; }

    public required string PublicAlias { get; init; }

    public required string StrategyProfileName { get; init; }

    public required int StrategyRevision { get; init; }

    public required Guid StrategyProfileId { get; init; }

    /// <summary>The four runtime limits stored for this bot instance.</summary>
    public required TradeProfile Profile { get; init; }

    /// <summary>When the overrides were last written, or null if there are none.</summary>
    public DateTimeOffset? LastWritten { get; init; }

    public required IReadOnlyList<StrategyParameterViewModel> StrategyParameters { get; init; }

    public required IReadOnlyList<StrategyProfileRevision> RevisionHistory { get; init; }

    /// <summary>Field name → message. Empty when nothing was refused.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True immediately after a successful save, so the screen can say the write landed.</summary>
    public bool JustSaved { get; init; }

    public bool SaveConflict { get; init; }

}

public sealed record StrategyParameterViewModel(
    StrategyParameterDefinition Definition,
    decimal Value,
    bool IsEnabled,
    string? Error);

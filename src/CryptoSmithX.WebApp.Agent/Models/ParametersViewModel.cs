using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>What the parameters screen renders. Four numbers, where each came from, and whatever
/// the last attempt to save them had to say.</summary>
public sealed class ParametersViewModel
{
    public required string BotInstanceId { get; init; }

    /// <summary>The values in the fields — an override where one exists, the deployed baseline otherwise.</summary>
    public required TradeProfile Profile { get; init; }

    /// <summary>Which of the four keys actually have an override row.</summary>
    public required IReadOnlySet<string> Overridden { get; init; }

    public required TradeProfileSource Source { get; init; }

    /// <summary>When the overrides were last written, or null if there are none.</summary>
    public DateTimeOffset? LastWritten { get; init; }

    /// <summary>Field name → message. Empty when nothing was refused.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True immediately after a successful save, so the screen can say the write landed.</summary>
    public bool JustSaved { get; init; }

    public bool IsOverridden(string key) => Overridden.Contains(key);
}

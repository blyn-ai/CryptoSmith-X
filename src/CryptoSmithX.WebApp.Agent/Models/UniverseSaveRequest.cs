namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>
/// The Universe form. Like <see cref="ParametersSaveRequest"/> it has no bot instance id: the row
/// written is the one belonging to the signed-in account, resolved on the server every time.
/// </summary>
public sealed class UniverseSaveRequest
{
    public int? AutoInstrumentCount { get; init; }

    public string? ForceIncludePairs { get; init; }

    public string? ForceExcludePairs { get; init; }

    /// <summary>The row's <c>updated_at</c> when the page was built, in UTC ticks — the optimistic
    /// check that keeps a stale form from overwriting a newer save.</summary>
    public long? UniverseVersion { get; init; }
}

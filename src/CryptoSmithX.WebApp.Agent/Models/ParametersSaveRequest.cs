namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>
/// The complete browser payload for the Strategy Modeler. It deliberately has
/// no bot instance id: ownership is resolved from the authenticated session.
/// </summary>
public sealed class ParametersSaveRequest
{
    public Guid? StrategyProfileId { get; init; }

    public int? StrategyRevision { get; init; }

    public decimal? PositionMarginUsd { get; init; }

    public decimal? Leverage { get; init; }

    public int? MaxOpenPositions { get; init; }

    public int? MaxOpenPositionsPerGroup { get; init; }

    public Dictionary<string, decimal?> Parameters { get; init; } = new(StringComparer.Ordinal);

    public string? ChangeNote { get; init; }
}

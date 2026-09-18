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

    /// <summary>The exit mode chosen on the page: false is fixed percentages, true is ATR trailing.
    /// NULL keeps the mode the active revision already has.</summary>
    public bool? AtrTrailingRegimeEnabled { get; init; }

    public Dictionary<string, decimal?> Parameters { get; init; } = new(StringComparer.Ordinal);

    public string? ChangeNote { get; init; }
}

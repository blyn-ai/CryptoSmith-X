namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// The four numbers a bot owner may set, and nothing else. The names are the override_key values
/// the worker reads; they are spelled here exactly once and every query below uses these constants,
/// so a fifth key cannot appear by a typo in a string literal.
/// </summary>
public static class TradeProfileKeys
{
    public const string PositionMarginUsd = "position_margin_usd";
    public const string Leverage = "leverage";
    public const string MaxOpenPositions = "max_open_positions";
    public const string MaxOpenPositionsPerGroup = "max_open_positions_per_group";

    public static readonly string[] All =
        [PositionMarginUsd, Leverage, MaxOpenPositions, MaxOpenPositionsPerGroup];
}

/// <summary>One owner's four numbers, whatever their source.</summary>
public sealed record TradeProfile(
    decimal PositionMarginUsd,
    decimal Leverage,
    int MaxOpenPositions,
    int MaxOpenPositionsPerGroup);

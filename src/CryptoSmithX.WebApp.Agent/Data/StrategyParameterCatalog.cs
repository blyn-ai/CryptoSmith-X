using System.Globalization;
using System.Text.Json.Nodes;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// The narrow, intentional Strategy Modeler write surface. Each definition
/// connects one user-facing field to one value inside a worker profile; it is
/// not a generic JSON editor and cannot reach protected worker configuration.
/// </summary>
public static class StrategyParameterCatalog
{
    public static readonly IReadOnlyList<StrategyParameterDefinition> All =
    [
        Decimal("markets", "Trading", "MaxActiveInstruments", "Markets evaluated", "markets", 1m, 100m, 1m, 0),
        Decimal("volume", "Trading", "StrongMoverMinDailyVolumeEur", "Minimum 24h volume", "USD", 1_000m, 100_000_000m, 1_000m, 0),
        Decimal("spread", "Strategy", "MaxEntrySpreadPercent", "Maximum entry spread", "%", 0.01m, 0.50m, 0.01m, 2),
        Decimal("longScore", "Strategy", "MinimumLongScore", "LONG score threshold", "score", 0.50m, 0.95m, 0.01m, 2),
        Decimal("shortScore", "Shorts", "MinShortScore", "SHORT score threshold", "score", 0.50m, 0.95m, 0.01m, 2),
        Decimal("btcDrop", "Regime", "BtcCrashPct", "BTC crash threshold", "%", -10m, -0.50m, 0.10m, 1, StrategyValueTransform.Negate),
        Decimal("btcBars", "Regime", "BtcCrashLookback", "BTC crash lookback", "candles", 1m, 24m, 1m, 0),
        Decimal("stop", "Exits", "StopAtrMult", "Stop loss", "x ATR", 0.50m, 3m, 0.05m, 2),
        Decimal("trail", "Exits", "TrailingActivationRMultiple", "Trail activation", "R", 0.30m, 3m, 0.10m, 1, StrategyValueTransform.Identity, "Exits.AtrTrailingRegimeEnabled"),
        Decimal("maxHold", "Exits", "MaxHoldMinutes", "Maximum hold", "min", 15m, 1_440m, 15m, 0),
        Decimal("cooldown", "ExecutionPolicy", "CooldownAfterStopLossSeconds", "Stop-loss cooldown", "min", 0m, 480m, 15m, 0, StrategyValueTransform.SecondsToMinutes),
    ];

    public static StrategyParameterDefinition Get(string id) =>
        All.SingleOrDefault(parameter => string.Equals(parameter.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown strategy parameter '{id}'.");

    public static bool IsEnabled(StrategyParameterDefinition definition, JsonObject values)
    {
        if (definition.EnabledWhenPath is null)
        {
            return true;
        }

        var parts = definition.EnabledWhenPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && values[parts[0]]?[parts[1]]?.GetValue<bool>() == true;
    }

    public static decimal Read(StrategyParameterDefinition definition, JsonObject values)
    {
        var raw = values[definition.Section]?[definition.Property]?.GetValue<decimal>()
            ?? throw new InvalidOperationException($"Strategy profile is missing '{definition.Section}.{definition.Property}'.");
        return definition.Transform.ToDisplay(raw);
    }

    public static void Write(StrategyParameterDefinition definition, JsonObject values, decimal displayValue)
    {
        if (!IsEnabled(definition, values))
        {
            throw new InvalidOperationException($"Strategy parameter '{definition.Id}' is not enabled by this profile.");
        }

        if (displayValue < definition.Minimum || displayValue > definition.Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayValue),
                displayValue,
                $"Strategy parameter '{definition.Id}' must be between {definition.Minimum} and {definition.Maximum}.");
        }

        if (definition.DecimalPlaces == 0 && displayValue != decimal.Truncate(displayValue))
        {
            throw new ArgumentException($"Strategy parameter '{definition.Id}' must be a whole number.", nameof(displayValue));
        }

        if (values[definition.Section] is not JsonObject section)
        {
            throw new InvalidOperationException($"Strategy profile is missing '{definition.Section}'.");
        }

        section[definition.Property] = definition.Transform.ToStored(displayValue);
    }

    private static StrategyParameterDefinition Decimal(
        string id,
        string section,
        string property,
        string label,
        string unit,
        decimal minimum,
        decimal maximum,
        decimal step,
        int decimalPlaces,
        StrategyValueTransform transform = StrategyValueTransform.Identity,
        string? enabledWhenPath = null) =>
        new(id, section, property, label, unit, minimum, maximum, step, decimalPlaces, transform, enabledWhenPath);
}

public sealed record StrategyParameterDefinition(
    string Id,
    string Section,
    string Property,
    string Label,
    string Unit,
    decimal Minimum,
    decimal Maximum,
    decimal Step,
    int DecimalPlaces,
    StrategyValueTransform Transform,
    string? EnabledWhenPath)
{
    public string Format(decimal value) => value.ToString($"F{DecimalPlaces}", CultureInfo.InvariantCulture);
}

public enum StrategyValueTransform
{
    Identity,
    Negate,
    SecondsToMinutes,
}

public static class StrategyValueTransformExtensions
{
    public static decimal ToDisplay(this StrategyValueTransform transform, decimal stored) => transform switch
    {
        StrategyValueTransform.Identity => stored,
        StrategyValueTransform.Negate => -stored,
        StrategyValueTransform.SecondsToMinutes => stored / 60m,
        _ => throw new ArgumentOutOfRangeException(nameof(transform), transform, null),
    };

    public static decimal ToStored(this StrategyValueTransform transform, decimal display) => transform switch
    {
        StrategyValueTransform.Identity => display,
        StrategyValueTransform.Negate => -display,
        StrategyValueTransform.SecondsToMinutes => display * 60m,
        _ => throw new ArgumentOutOfRangeException(nameof(transform), transform, null),
    };
}

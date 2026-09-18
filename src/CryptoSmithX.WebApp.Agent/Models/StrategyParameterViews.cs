using System.Text.Json.Nodes;
using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>
/// The parameter cards and the exit mode as the page draws them, from the resolved profile and
/// whatever a refused save posted. Pure, so what each bot's page shows is testable without a page.
/// </summary>
public static class StrategyParameterViews
{
    public const string MarketsLockReason =
        "Kiek porų renkama automatiškai, nustato skiltis „Prekybos poros“ žemiau: worker šią profilio reikšmę pakeičia jos skaičiumi.";

    /// <summary>The mode on screen is the saved one, unless a refused save asked for the other: then
    /// the page shows what was asked, with the fields that go with it.</summary>
    public static ExitModeViewModel ExitMode(JsonObject values, ParametersSaveRequest? posted)
    {
        var saved = StrategyParameterCatalog.EffectiveAtrMode(values);
        var editable = StrategyParameterCatalog.ReadAtrMode(values) is not null;
        var shown = editable && posted?.AtrTrailingRegimeEnabled is { } asked ? asked : saved;
        return new ExitModeViewModel(shown, saved, editable);
    }

    /// <param name="universeAutoInstrumentCount">The Universe row's automatic count, or NULL when the
    /// bot has no row. With a row, the worker runs that count instead of the profile's markets value.</param>
    public static IReadOnlyList<StrategyParameterViewModel> Build(
        JsonObject values,
        bool atrMode,
        ParametersSaveRequest? posted,
        IReadOnlyDictionary<string, string>? errors,
        int? universeAutoInstrumentCount) =>
        StrategyParameterCatalog.All
            .Select(definition =>
            {
                var decidedByUniverse = universeAutoInstrumentCount is not null
                    && definition.Id == StrategyParameterCatalog.MarketsId;
                return new StrategyParameterViewModel(
                    definition,
                    decidedByUniverse
                        ? universeAutoInstrumentCount
                        : posted?.Parameters.GetValueOrDefault(definition.Id) ?? StrategyParameterCatalog.Read(definition, values),
                    StrategyParameterCatalog.IsActiveIn(definition, atrMode),
                    errors?.GetValueOrDefault(definition.Id),
                    decidedByUniverse ? MarketsLockReason : null);
            })
            .ToList();
}

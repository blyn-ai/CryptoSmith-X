using System.Text.Json.Nodes;
using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>
/// What one post of the Parameters form would write, decided without a database: the exit mode,
/// the parameters of that mode, and every reason to refuse. The controller only loads, calls this,
/// and stores — so the rules the tests hold are the rules the page runs.
/// </summary>
public static class ParametersSavePlan
{
    public static ParametersSavePlanResult Build(
        ParametersSaveRequest request,
        ActiveStrategyProfile strategy,
        bool universeDecidesMarkets)
    {
        var errors = ValidateRuntimeLimits(request);
        if (request.StrategyProfileId != strategy.ProfileId || request.StrategyRevision != strategy.Revision)
        {
            errors["strategy"] = "Strategija pasikeitė, kol buvo atidarytas šis puslapis. Prieš išsaugant peržiūrėk dabartinę reviziją.";
        }

        if (request.ChangeNote?.Length > 1_000)
        {
            errors["changeNote"] = "Strategijos pastaba negali būti ilgesnė nei 1000 simbolių";
        }

        var values = StrategyProfileStore.ResolveValues(strategy);

        // The mode switch is only honoured when the profile carries it. Without it the worker runs
        // its default, fixed percentages, and a post cannot add the key.
        var modeEditable = StrategyParameterCatalog.ReadAtrMode(values) is not null;
        var atrMode = modeEditable
            ? request.AtrTrailingRegimeEnabled ?? StrategyParameterCatalog.EffectiveAtrMode(values)
            : StrategyParameterCatalog.EffectiveAtrMode(values);
        if (modeEditable)
        {
            StrategyParameterCatalog.WriteExitMode(values, atrMode);
        }

        var parameters = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var definition in Writable(values, atrMode, universeDecidesMarkets))
        {
            // Only the chosen mode's fields are asked for. The other mode's inputs are disabled on
            // the page, so their absence from the post is the page working, not a gap to complain
            // about — and their stored values are carried into the new revision untouched.
            if (!request.Parameters.TryGetValue(definition.Id, out var value) || value is null)
            {
                errors[definition.Id] = "Įvesk skaičių";
                continue;
            }

            try
            {
                StrategyParameterCatalog.Write(definition, values, value.Value);
                parameters[definition.Id] = value.Value;
            }
            catch (ArgumentException)
            {
                errors[definition.Id] = "Reikšmė nepatenka į leidžiamą ribą";
            }
        }

        return new ParametersSavePlanResult(
            errors,
            atrMode,
            modeEditable ? atrMode : null,
            parameters);
    }

    /// <summary>
    /// The parameters a save writes: the profile carries the key, the key belongs to the exit mode
    /// being saved, and nothing else decides it. A key the profile lacks has no input on the page,
    /// so it was never askable; the markets count is decided by the Universe row when one exists.
    /// </summary>
    public static IEnumerable<StrategyParameterDefinition> Writable(
        JsonObject values,
        bool atrMode,
        bool universeDecidesMarkets) =>
        StrategyParameterCatalog.All.Where(definition =>
            StrategyParameterCatalog.IsPresent(definition, values)
            && StrategyParameterCatalog.IsActiveIn(definition, atrMode)
            && !(universeDecidesMarkets && definition.Id == StrategyParameterCatalog.MarketsId));

    /// <summary>
    /// The same four rules the fields carry, checked again on the server. Null means the field was
    /// not a number at all — an empty box, or letters — and is refused rather than read as zero.
    /// </summary>
    private static Dictionary<string, string> ValidateRuntimeLimits(ParametersSaveRequest request)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        // ZERO IS ALLOWED, and it is the one value here that means something other than a size:
        // no margin is no position to open. It used to be refused as a typo, which left an owner
        // wanting to stop new entries with nothing on this screen to do it with. The bot's own
        // table has no CHECK against it (bot_config_overrides, verified on the test host), so the
        // value stores; what the screen owes the reader is to say loudly what it now means.
        if (request.PositionMarginUsd is not { } margin || margin < 0)
        {
            errors[TradeProfileKeys.PositionMarginUsd] = "Įvesk nulį arba teigiamą skaičių";
        }

        if (request.Leverage is not { } lev || lev < 1 || lev > 10)
        {
            errors[TradeProfileKeys.Leverage] = "Leistina reikšmė nuo 1 iki 10";
        }

        if (request.MaxOpenPositions is not { } max || max < 1 || max > 20)
        {
            errors[TradeProfileKeys.MaxOpenPositions] = "Įvesk sveiką skaičių nuo 1 iki 20";
        }

        if (request.MaxOpenPositionsPerGroup is not { } group || group < 1 || group > 20)
        {
            errors[TradeProfileKeys.MaxOpenPositionsPerGroup] = "Įvesk sveiką skaičių nuo 1 iki 20";
        }
        else if (request.MaxOpenPositions is { } total && group > total)
        {
            // Only when the total itself is valid: two complaints about one mistake is one too many.
            errors[TradeProfileKeys.MaxOpenPositionsPerGroup] = "Negali viršyti bendro pozicijų limito";
        }

        return errors;
    }
}

/// <param name="AtrMode">The mode the post asked for — what a refused save shows again.</param>
/// <param name="ModeToWrite">The switch value to store, or NULL when the profile does not carry it.</param>
/// <param name="Parameters">Display values keyed by catalogue id, only for the chosen mode.</param>
public sealed record ParametersSavePlanResult(
    Dictionary<string, string> Errors,
    bool AtrMode,
    bool? ModeToWrite,
    IReadOnlyDictionary<string, decimal> Parameters);

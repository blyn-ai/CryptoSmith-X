using System.Text.Json.Nodes;
using CryptoSmithX.WebApp.Agent.Data;
using CryptoSmithX.WebApp.Agent.Models;

namespace CryptoSmithX.WebApp.Agent.Tests;

/// <summary>
/// The exit mode is one switch in the profile, <c>Exits.AtrTrailingRegimeEnabled</c>, and the page
/// shows and saves only the fields of the mode that switch selects. The fixtures are the two real
/// profiles on the bot database: LUKO runs fixed percentages, BYKO runs ATR trailing.
/// </summary>
public sealed class ExitModeTests
{
    private static readonly string[] FixedIds = ["fixedStop", "fixedTakeProfit", "fixedTrail"];

    private static readonly string[] AtrIds = ["stop", "trail", "atrTrail"];

    private static ActiveStrategyProfile Luko() => Profile(
        "LUKO current strategy",
        7,
        """
        {
          "Trading": { "MaxActiveInstruments": 50 },
          "TpSl": { "StopLossPercent": 2, "TakeProfitPercent": 4, "TrailingStopPercent": 0.75 },
          "Exits": {
            "AtrTrailingRegimeEnabled": false,
            "StopAtrMult": 1.00,
            "TrailingActivationRMultiple": 0,
            "TrailingAtrMultiple": 0,
            "MaxHoldMinutes": 360
          },
          "ExecutionPolicy": { "CooldownAfterStopLossSeconds": 14400 }
        }
        """);

    private static ActiveStrategyProfile Byko() => Profile(
        "BYKO current strategy",
        9,
        """
        {
          "Trading": { "MaxActiveInstruments": 78 },
          "TpSl": { "StopLossPercent": 1.75, "TakeProfitPercent": 3.5, "TrailingStopPercent": 0.5 },
          "Exits": {
            "AtrTrailingRegimeEnabled": true,
            "StopAtrMult": 1.25,
            "TrailingActivationRMultiple": 1.0,
            "TrailingAtrMultiple": 1.5,
            "MaxHoldMinutes": 360
          },
          "ExecutionPolicy": { "CooldownAfterStopLossSeconds": 14400 }
        }
        """);

    [Fact]
    public void Luko_renders_the_fixed_percentages_and_not_the_atr_controls()
    {
        var values = StrategyProfileStore.ResolveValues(Luko());
        var mode = StrategyParameterViews.ExitMode(values, posted: null);
        var cards = StrategyParameterViews.Build(values, mode.AtrMode, posted: null, errors: null, universeAutoInstrumentCount: null)
            .ToDictionary(card => card.Definition.Id);

        Assert.False(mode.AtrMode);
        Assert.True(mode.Editable);
        Assert.All(FixedIds, id => Assert.True(cards[id].IsEnabled, id));
        Assert.All(AtrIds, id => Assert.False(cards[id].IsEnabled, id));
        Assert.Equal(2m, cards["fixedStop"].Value);
        Assert.Equal(4m, cards["fixedTakeProfit"].Value);
        Assert.Equal(0.75m, cards["fixedTrail"].Value);
    }

    [Fact]
    public void Luko_saves_the_fixed_percentages_and_nothing_of_the_atr_mode()
    {
        // Every catalogue field is posted, the ATR ones included — as a tampered form would. Only
        // the fixed trio may land.
        var active = Luko();
        var request = Request(active, atrMode: false, new()
        {
            ["fixedStop"] = 2.5m, ["fixedTakeProfit"] = 5m, ["fixedTrail"] = 1m,
            ["stop"] = 2m, ["trail"] = 2m, ["atrTrail"] = 2m,
        });

        var plan = ParametersSavePlan.Build(request, active, universeDecidesMarkets: true);
        var next = StrategyProfileStore.BuildNextRevisionValues(active, plan.Parameters, plan.ModeToWrite);

        Assert.Empty(plan.Errors);
        Assert.False(plan.ModeToWrite);
        Assert.All(FixedIds, id => Assert.Contains(id, plan.Parameters.Keys));
        Assert.All(AtrIds, id => Assert.DoesNotContain(id, plan.Parameters.Keys));
        AssertTpSl(next, 2.5m, 5m, 1m);
        AssertAtr(next, enabled: false, stop: 1.00m, activation: 0m, trail: 0m);
    }

    [Fact]
    public void Byko_renders_the_three_atr_controls_and_no_fixed_take_profit()
    {
        var values = StrategyProfileStore.ResolveValues(Byko());
        var mode = StrategyParameterViews.ExitMode(values, posted: null);
        var cards = StrategyParameterViews.Build(values, mode.AtrMode, posted: null, errors: null, universeAutoInstrumentCount: null)
            .ToDictionary(card => card.Definition.Id);

        Assert.True(mode.AtrMode);
        Assert.All(AtrIds, id => Assert.True(cards[id].IsEnabled, id));
        Assert.All(FixedIds, id => Assert.False(cards[id].IsEnabled, id));
        Assert.Equal(1.25m, cards["stop"].Value);
        Assert.Equal(1.0m, cards["trail"].Value);
        Assert.Equal(1.5m, cards["atrTrail"].Value);
    }

    [Fact]
    public void Byko_saves_the_atr_parameters_and_leaves_the_fixed_take_profit_alone()
    {
        var active = Byko();
        var request = Request(active, atrMode: true, new()
        {
            ["stop"] = 1.5m, ["trail"] = 1.2m, ["atrTrail"] = 2m,
            ["fixedStop"] = 9m, ["fixedTakeProfit"] = 9m, ["fixedTrail"] = 3m,
        });

        var plan = ParametersSavePlan.Build(request, active, universeDecidesMarkets: true);
        var next = StrategyProfileStore.BuildNextRevisionValues(active, plan.Parameters, plan.ModeToWrite);

        Assert.Empty(plan.Errors);
        Assert.True(plan.ModeToWrite);
        Assert.All(AtrIds, id => Assert.Contains(id, plan.Parameters.Keys));
        Assert.All(FixedIds, id => Assert.DoesNotContain(id, plan.Parameters.Keys));
        AssertAtr(next, enabled: true, stop: 1.5m, activation: 1.2m, trail: 2m);
        AssertTpSl(next, 1.75m, 3.5m, 0.5m);
    }

    [Fact]
    public void Switching_luko_to_atr_keeps_its_fixed_percentages_stored()
    {
        var active = Luko();
        var request = Request(active, atrMode: true, new() { ["stop"] = 1.25m, ["trail"] = 1m, ["atrTrail"] = 1.5m });

        var plan = ParametersSavePlan.Build(request, active, universeDecidesMarkets: true);
        var next = StrategyProfileStore.BuildNextRevisionValues(active, plan.Parameters, plan.ModeToWrite);

        Assert.Empty(plan.Errors);
        AssertAtr(next, enabled: true, stop: 1.25m, activation: 1m, trail: 1.5m);
        AssertTpSl(next, 2m, 4m, 0.75m);
    }

    [Fact]
    public void Switching_byko_to_fixed_keeps_its_atr_values_stored()
    {
        var active = Byko();
        var request = Request(active, atrMode: false, new() { ["fixedStop"] = 2m, ["fixedTakeProfit"] = 4m, ["fixedTrail"] = 0.75m });

        var plan = ParametersSavePlan.Build(request, active, universeDecidesMarkets: true);
        var next = StrategyProfileStore.BuildNextRevisionValues(active, plan.Parameters, plan.ModeToWrite);

        Assert.Empty(plan.Errors);
        AssertTpSl(next, 2m, 4m, 0.75m);
        AssertAtr(next, enabled: false, stop: 1.25m, activation: 1.0m, trail: 1.5m);
    }

    [Fact]
    public void Switching_to_atr_asks_for_atr_values_and_refuses_the_stored_zeros()
    {
        // LUKO's ATR keys hold 0 — "not used" to the worker. Switching on must not carry them in.
        var active = Luko();

        var unposted = ParametersSavePlan.Build(Request(active, atrMode: true, new()), active, universeDecidesMarkets: true);
        var zeros = ParametersSavePlan.Build(
            Request(active, atrMode: true, new() { ["stop"] = 1m, ["trail"] = 0m, ["atrTrail"] = 0m }),
            active,
            universeDecidesMarkets: true);

        Assert.All(AtrIds, id => Assert.Equal("Įvesk skaičių", unposted.Errors[id]));
        Assert.Equal("Reikšmė nepatenka į leidžiamą ribą", zeros.Errors["trail"]);
        Assert.Equal("Reikšmė nepatenka į leidžiamą ribą", zeros.Errors["atrTrail"]);
        Assert.All(FixedIds, id => Assert.False(unposted.Errors.ContainsKey(id), id));
    }

    [Fact]
    public void A_refused_switch_is_shown_in_the_mode_that_was_asked_for()
    {
        var values = StrategyProfileStore.ResolveValues(Luko());
        var posted = Request(Luko(), atrMode: true, new());

        var mode = StrategyParameterViews.ExitMode(values, posted);

        Assert.True(mode.AtrMode);
        Assert.False(mode.SavedAtrMode);
    }

    [Fact]
    public void The_store_refuses_a_parameter_of_the_mode_being_left()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StrategyProfileStore.BuildNextRevisionValues(
                Byko(),
                new Dictionary<string, decimal> { ["stop"] = 1.5m },
                atrTrailingRegimeEnabled: false));

        Assert.Contains("not enabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_without_the_switch_runs_fixed_and_cannot_be_given_one()
    {
        var active = Profile("Old profile", 1,
            """
            { "TpSl": { "StopLossPercent": 2, "TakeProfitPercent": 4, "TrailingStopPercent": 0.75 },
              "Exits": { "StopAtrMult": 1 } }
            """);
        var values = StrategyProfileStore.ResolveValues(active);

        var plan = ParametersSavePlan.Build(
            Request(active, atrMode: true, new() { ["fixedStop"] = 2m, ["fixedTakeProfit"] = 4m, ["fixedTrail"] = 0.75m }),
            active,
            universeDecidesMarkets: false);

        Assert.False(StrategyParameterCatalog.EffectiveAtrMode(values));
        Assert.False(StrategyParameterViews.ExitMode(values, posted: null).Editable);
        Assert.Throws<InvalidOperationException>(() => StrategyParameterCatalog.WriteExitMode(values, true));
        Assert.Empty(plan.Errors);
        Assert.Null(plan.ModeToWrite);
        Assert.False(StrategyProfileStore.BuildNextRevisionValues(active, plan.Parameters, plan.ModeToWrite)["Exits"]!
            .AsObject().ContainsKey("AtrTrailingRegimeEnabled"));
    }

    internal static ActiveStrategyProfile Profile(string name, int revision, string json) => new(
        Guid.NewGuid(),
        name,
        revision,
        JsonNode.Parse(json)!.AsObject(),
        new JsonObject(),
        null,
        DateTime.UtcNow);

    /// <summary>A valid post for <paramref name="active"/>: runtime limits in range, the page's own
    /// profile id and revision, and the given exit mode and parameter values.</summary>
    internal static ParametersSaveRequest Request(
        ActiveStrategyProfile active,
        bool? atrMode,
        Dictionary<string, decimal?> parameters)
    {
        var values = StrategyProfileStore.ResolveValues(active);
        foreach (var definition in StrategyParameterCatalog.All.Where(d => d.Mode == StrategyExitMode.Always))
        {
            if (StrategyParameterCatalog.Read(definition, values) is { } stored)
            {
                parameters.TryAdd(definition.Id, stored);
            }
        }

        return new ParametersSaveRequest
        {
            StrategyProfileId = active.ProfileId,
            StrategyRevision = active.Revision,
            PositionMarginUsd = 25m,
            Leverage = 3m,
            MaxOpenPositions = 5,
            MaxOpenPositionsPerGroup = 2,
            AtrTrailingRegimeEnabled = atrMode,
            Parameters = new Dictionary<string, decimal?>(parameters, StringComparer.Ordinal),
        };
    }

    private static void AssertTpSl(JsonObject values, decimal stop, decimal takeProfit, decimal trailing)
    {
        Assert.Equal(stop, values["TpSl"]!["StopLossPercent"]!.GetValue<decimal>());
        Assert.Equal(takeProfit, values["TpSl"]!["TakeProfitPercent"]!.GetValue<decimal>());
        Assert.Equal(trailing, values["TpSl"]!["TrailingStopPercent"]!.GetValue<decimal>());
    }

    private static void AssertAtr(JsonObject values, bool enabled, decimal stop, decimal activation, decimal trail)
    {
        Assert.Equal(enabled, values["Exits"]!["AtrTrailingRegimeEnabled"]!.GetValue<bool>());
        Assert.Equal(stop, values["Exits"]!["StopAtrMult"]!.GetValue<decimal>());
        Assert.Equal(activation, values["Exits"]!["TrailingActivationRMultiple"]!.GetValue<decimal>());
        Assert.Equal(trail, values["Exits"]!["TrailingAtrMultiple"]!.GetValue<decimal>());
    }
}

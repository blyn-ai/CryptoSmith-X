using System.Reflection;
using CryptoSmithX.WebApp.Agent.Controllers;
using CryptoSmithX.WebApp.Agent.Data;
using CryptoSmithX.WebApp.Agent.Models;

namespace CryptoSmithX.WebApp.Agent.Tests;

/// <summary>
/// A signed-in account writes only its own bot. The bot is resolved from the username on every
/// request; nothing the browser sends can name another one, and every write is keyed by the id
/// that lookup returned.
/// </summary>
public sealed class OwnershipTests
{
    [Fact]
    public void No_form_the_browser_posts_can_name_a_bot()
    {
        foreach (var type in new[] { typeof(ParametersSaveRequest), typeof(UniverseSaveRequest), typeof(KrakenCredentialRequest) })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(
                    NamesAnOwner(property.Name),
                    $"{type.Name}.{property.Name} would let a post choose which bot it writes to.");
            }
        }
    }

    [Fact]
    public void No_action_takes_a_bot_from_the_request()
    {
        var actions = typeof(ParametersController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Contains(actions, action => action.Name == nameof(ParametersController.SaveUniverse));
        foreach (var parameter in actions.SelectMany(action => action.GetParameters()))
        {
            Assert.False(NamesAnOwner(parameter.Name!), $"Action parameter '{parameter.Name}' would come from the request.");
        }
    }

    [Fact]
    public void The_bot_is_the_one_the_username_resolves_to()
    {
        Assert.Contains("webapp_username = @username", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
        Assert.Contains("is_active = true", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Universe_reads_and_writes_are_keyed_by_the_resolved_owner_only()
    {
        Assert.Contains("where bot_instance_id = @botInstanceId", UniversePreferenceStore.LoadSql, StringComparison.Ordinal);
        Assert.Contains("where bot_instance_id = @botInstanceId", UniversePreferenceStore.UpdateSql, StringComparison.Ordinal);

        // An update of an existing row, never an insert: a post cannot create a row for any bot.
        Assert.DoesNotContain("insert", UniversePreferenceStore.UpdateSql, StringComparison.OrdinalIgnoreCase);

        // The store takes the owner record the username lookup returned, not a free string.
        foreach (var method in new[] { nameof(UniversePreferenceStore.LoadAsync), nameof(UniversePreferenceStore.SaveAsync) })
        {
            var parameters = typeof(UniversePreferenceStore).GetMethod(method)!.GetParameters();
            Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(BotInstanceOwner));
            Assert.DoesNotContain(parameters, parameter =>
                parameter.ParameterType == typeof(string) && NamesAnOwner(parameter.Name!));
        }
    }

    [Fact]
    public void Profile_writes_are_keyed_by_the_resolved_owner_only()
    {
        Assert.Contains("where bot_instance_id = @botInstanceId", StrategyProfileStore.LockActiveAssignmentSql, StringComparison.Ordinal);
        Assert.Contains("where bot_instance_id = @botInstanceId", StrategyProfileStore.ActivateRevisionSql, StringComparison.Ordinal);
        Assert.Contains("and profile_id = @profileId", StrategyProfileStore.ActivateRevisionSql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_posted_profile_of_another_bot_is_refused_before_anything_is_written()
    {
        // LUKO's page posting BYKO's profile id and revision — the only handle a form has on a
        // profile. The plan refuses it, so the save never reaches the store.
        var luko = ExitModeTests.Profile("LUKO current strategy", 7,
            """{ "TpSl": { "StopLossPercent": 2, "TakeProfitPercent": 4, "TrailingStopPercent": 0.75 }, "Exits": { "AtrTrailingRegimeEnabled": false } }""");
        var byko = ExitModeTests.Profile("BYKO current strategy", 9, "{}");
        var request = ExitModeTests.Request(luko, atrMode: false, new()
        {
            ["fixedStop"] = 2m, ["fixedTakeProfit"] = 4m, ["fixedTrail"] = 0.75m,
        });
        var tampered = new ParametersSaveRequest
        {
            StrategyProfileId = byko.ProfileId,
            StrategyRevision = byko.Revision,
            PositionMarginUsd = request.PositionMarginUsd,
            Leverage = request.Leverage,
            MaxOpenPositions = request.MaxOpenPositions,
            MaxOpenPositionsPerGroup = request.MaxOpenPositionsPerGroup,
            AtrTrailingRegimeEnabled = request.AtrTrailingRegimeEnabled,
            Parameters = request.Parameters,
        };

        Assert.Empty(ParametersSavePlan.Build(request, luko, universeDecidesMarkets: true).Errors);
        Assert.True(ParametersSavePlan.Build(tampered, luko, universeDecidesMarkets: true).Errors.ContainsKey("strategy"));
    }

    private static bool NamesAnOwner(string name) =>
        name.Contains("instance", StringComparison.OrdinalIgnoreCase)
        || name.Contains("owner", StringComparison.OrdinalIgnoreCase)
        || name.Contains("username", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("bot", StringComparison.OrdinalIgnoreCase);
}

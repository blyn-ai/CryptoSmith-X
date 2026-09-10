using System.Text.Json.Nodes;
using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Tests;

public sealed class StrategyProfileStoreTests
{
    [Fact]
    public void Instance_overrides_win_without_erasing_profile_values()
    {
        var profile = new ActiveStrategyProfile(
            Guid.Parse("e798459b-8e62-4f96-87e7-3291f1830dde"),
            "BYKO current strategy",
            1,
            JsonNode.Parse(
                """
                {
                  "Strategy": { "MinimumLongScore": 0.80, "MaxEntrySpreadPercent": 0.25 },
                  "Exits": { "StopAtrMult": 1.25 }
                }
                """)!.AsObject(),
            JsonNode.Parse(
                """
                {
                  "Strategy": { "MinimumLongScore": 0.85 },
                  "Exits": { "MaxHoldMinutes": 240 }
                }
                """)!.AsObject(),
            null,
            DateTime.UtcNow);

        var resolved = StrategyProfileStore.ResolveValues(profile);

        Assert.Equal(0.85m, resolved["Strategy"]!["MinimumLongScore"]!.GetValue<decimal>());
        Assert.Equal(0.25m, resolved["Strategy"]!["MaxEntrySpreadPercent"]!.GetValue<decimal>());
        Assert.Equal(1.25m, resolved["Exits"]!["StopAtrMult"]!.GetValue<decimal>());
        Assert.Equal(240, resolved["Exits"]!["MaxHoldMinutes"]!.GetValue<int>());
    }

    [Fact]
    public void Next_revision_folds_overrides_and_preserves_unedited_values()
    {
        var active = new ActiveStrategyProfile(
            Guid.Parse("e798459b-8e62-4f96-87e7-3291f1830dde"),
            "BYKO current strategy",
            7,
            JsonNode.Parse(
                """
                {
                  "Strategy": { "MinimumLongScore": 0.80, "MaxEntrySpreadPercent": 0.25 },
                  "Exits": { "AtrTrailingRegimeEnabled": true, "StopAtrMult": 1.25 }
                }
                """)!.AsObject(),
            JsonNode.Parse(
                """
                {
                  "Strategy": { "MinimumLongScore": 0.85 }
                }
                """)!.AsObject(),
            "Earlier revision",
            DateTime.UtcNow);

        var next = StrategyProfileStore.BuildNextRevisionValues(
            active,
            new Dictionary<string, decimal> { ["stop"] = 1.50m });

        Assert.Equal(0.85m, next["Strategy"]!["MinimumLongScore"]!.GetValue<decimal>());
        Assert.Equal(0.25m, next["Strategy"]!["MaxEntrySpreadPercent"]!.GetValue<decimal>());
        Assert.Equal(1.50m, next["Exits"]!["StopAtrMult"]!.GetValue<decimal>());
    }

    [Fact]
    public void Next_revision_refuses_a_disabled_trailing_control()
    {
        var active = new ActiveStrategyProfile(
            Guid.NewGuid(),
            "LUKO current strategy",
            2,
            JsonNode.Parse(
                """
                {
                  "Exits": { "AtrTrailingRegimeEnabled": false, "TrailingActivationRMultiple": 1.0 }
                }
                """)!.AsObject(),
            new JsonObject(),
            null,
            DateTime.UtcNow);

        var error = Assert.Throws<InvalidOperationException>(() =>
            StrategyProfileStore.BuildNextRevisionValues(
                active,
                new Dictionary<string, decimal> { ["trail"] = 1.50m }));

        Assert.Contains("not enabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_owner_lookup_is_scoped_to_the_authenticated_username()
    {
        Assert.Contains("webapp_username = @username", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
        Assert.Contains("is_active = true", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
        Assert.DoesNotContain("bot_instance_id = @botInstanceId", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
    }
}

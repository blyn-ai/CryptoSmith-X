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
    public void Active_owner_lookup_is_scoped_to_the_authenticated_username()
    {
        Assert.Contains("webapp_username = @username", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
        Assert.Contains("is_active = true", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
        Assert.DoesNotContain("bot_instance_id = @botInstanceId", BotInstanceOwnerStore.FindActiveSql, StringComparison.Ordinal);
    }
}

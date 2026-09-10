using System.Text.Json.Nodes;
using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Tests;

public sealed class StrategyParameterCatalogTests
{
    [Fact]
    public void Btc_crash_threshold_is_negative_in_the_ui_and_positive_in_the_worker_profile()
    {
        var values = JsonNode.Parse("""{ "Regime": { "BtcCrashPct": 2.0 } }""")!.AsObject();
        var definition = StrategyParameterCatalog.Get("btcDrop");

        Assert.Equal(-2.0m, StrategyParameterCatalog.Read(definition, values));

        StrategyParameterCatalog.Write(definition, values, -3.5m);

        Assert.Equal(3.5m, values["Regime"]!["BtcCrashPct"]!.GetValue<decimal>());
    }

    [Fact]
    public void Stop_loss_cooldown_is_shown_in_minutes_and_stored_in_seconds()
    {
        var values = JsonNode.Parse("""{ "ExecutionPolicy": { "CooldownAfterStopLossSeconds": 14400 } }""")!.AsObject();
        var definition = StrategyParameterCatalog.Get("cooldown");

        Assert.Equal(240m, StrategyParameterCatalog.Read(definition, values));

        StrategyParameterCatalog.Write(definition, values, 60m);

        Assert.Equal(3600m, values["ExecutionPolicy"]!["CooldownAfterStopLossSeconds"]!.GetValue<decimal>());
    }

    [Fact]
    public void Trailing_activation_cannot_be_changed_when_the_atr_regime_is_off()
    {
        var values = JsonNode.Parse(
            """
            { "Exits": { "AtrTrailingRegimeEnabled": false, "TrailingActivationRMultiple": 1.0 } }
            """)!.AsObject();
        var definition = StrategyParameterCatalog.Get("trail");

        Assert.False(StrategyParameterCatalog.IsEnabled(definition, values));
        Assert.Throws<InvalidOperationException>(() => StrategyParameterCatalog.Write(definition, values, 1.5m));
    }
}

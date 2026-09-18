using CryptoSmithX.WebApp.Agent.Data;
using CryptoSmithX.WebApp.Agent.Models;

namespace CryptoSmithX.WebApp.Agent.Tests;

/// <summary>
/// The Universe form: pair lists as typed, stored as the worker reads them. The registry here is a
/// slice of the real one — enabled futures pairs are upper case with a slash.
/// </summary>
public sealed class UniverseTests
{
    private static readonly IReadOnlySet<string> Registry =
        new HashSet<string>(["XBT/USD", "ETH/USD", "SOL/USD", "XRP/USD", "DOGE/USD", "1INCH/USD"], StringComparer.Ordinal);

    [Fact]
    public void Pairs_split_on_commas_semicolons_and_newlines_and_come_out_normalised()
    {
        var pairs = UniversePairList.Parse(" xbt/usd, ETH/USD;sol/usd\r\n\nXBT/USD ;; , Eth/Usd\n1inch/usd ");

        Assert.Equal(["XBT/USD", "ETH/USD", "SOL/USD", "1INCH/USD"], pairs);
    }

    [Fact]
    public void An_empty_box_is_an_empty_list()
    {
        Assert.Empty(UniversePairList.Parse(null));
        Assert.Empty(UniversePairList.Parse(" ,;\n\r "));
    }

    [Fact]
    public void A_stored_list_goes_back_into_the_box_and_reads_the_same()
    {
        string[] stored = ["XBT/USD", "ETH/USD", "SOL/USD"];

        Assert.Equal(stored, UniversePairList.Parse(UniversePairList.Format(stored)));
    }

    [Fact]
    public void A_pair_in_both_lists_is_refused()
    {
        var result = UniversePairList.Validate(40, "XBT/USD, eth/usd", "ETH/USD; doge/usd", Registry);

        Assert.Null(result.Selection);
        Assert.Equal("Pora negali būti abiejuose sąrašuose: ETH/USD", result.Errors[UniverseFields.ForceExcludePairs]);
    }

    [Fact]
    public void Pairs_the_futures_registry_does_not_hold_are_named_and_refused()
    {
        var result = UniversePairList.Validate(40, "XBT/USD, BTC/USD", "PF_XBTUSD", Registry);

        Assert.Null(result.Selection);
        Assert.Equal("Tokių futures porų registre nėra: BTC/USD", result.Errors[UniverseFields.ForceIncludePairs]);
        Assert.Equal("Tokių futures porų registre nėra: PF_XBTUSD", result.Errors[UniverseFields.ForceExcludePairs]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(78)]
    [InlineData(500)]
    public void Automatic_counts_from_zero_to_five_hundred_are_valid(int count)
    {
        var result = UniversePairList.Validate(count, "xbt/usd\neth/usd", "", Registry);

        Assert.Empty(result.Errors);
        Assert.Equal(count, result.Selection!.AutoInstrumentCount);
        Assert.Equal(["XBT/USD", "ETH/USD"], result.Selection.ForceIncludePairs);
        Assert.Empty(result.Selection.ForceExcludePairs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(501)]
    public void Automatic_counts_outside_the_table_check_are_refused(int? count)
    {
        var result = UniversePairList.Validate(count, "", "", Registry);

        Assert.Null(result.Selection);
        Assert.Equal("Įvesk sveiką skaičių nuo 0 iki 500", result.Errors[UniverseFields.AutoInstrumentCount]);
    }

    [Fact]
    public void With_a_universe_row_the_markets_count_is_shown_from_it_and_never_saved_from_the_profile()
    {
        // The worker applies the Universe row after the profile, so the profile's markets value is
        // overwritten every cycle. The page shows the figure that runs and does not write the other.
        var active = ExitModeTests.Profile("LUKO current strategy", 7,
            """
            { "Trading": { "MaxActiveInstruments": 50 },
              "TpSl": { "StopLossPercent": 2, "TakeProfitPercent": 4, "TrailingStopPercent": 0.75 },
              "Exits": { "AtrTrailingRegimeEnabled": false } }
            """);
        var request = ExitModeTests.Request(active, atrMode: false, new()
        {
            ["markets"] = 99m, ["fixedStop"] = 2m, ["fixedTakeProfit"] = 4m, ["fixedTrail"] = 0.75m,
        });

        var plan = ParametersSavePlan.Build(request, active, universeDecidesMarkets: true);
        var markets = StrategyParameterViews
            .Build(StrategyProfileStore.ResolveValues(active), atrMode: false, posted: null, errors: null, universeAutoInstrumentCount: 78)
            .Single(card => card.Definition.Id == StrategyParameterCatalog.MarketsId);

        Assert.DoesNotContain(StrategyParameterCatalog.MarketsId, plan.Parameters.Keys);
        Assert.Equal(50m, StrategyProfileStore.BuildNextRevisionValues(active, plan.Parameters, plan.ModeToWrite)["Trading"]!["MaxActiveInstruments"]!.GetValue<decimal>());
        Assert.Equal(78m, markets.Value);
        Assert.Equal(StrategyParameterViews.MarketsLockReason, markets.LockReason);
    }
}

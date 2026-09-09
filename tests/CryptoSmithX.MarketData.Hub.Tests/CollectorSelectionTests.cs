using CryptoSmithX.MarketData.Hub.Ingestion;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// <see cref="ExchangeWorker.DesiredCollectors"/> is the 0014 replacement for the old fixed
/// five-loop array: which collector loops an exchange gets is data (policy mode × declared
/// capability), not code. These tests drive it directly, with no supervisor and no database.
/// </summary>
public sealed class CollectorSelectionTests
{
    /// <summary>Every dataset that HAS a loop, taken from the list itself rather than spelled out:
    /// this array is the premise of the first test ("implements everything"), and writing it by hand
    /// meant the premise quietly stopped being true the moment a sixth loop was added.</summary>
    private static readonly string[] AllImplemented = [.. ExchangeWorker.KnownCollectorDatasets];

    [Fact]
    public void An_exchange_that_implements_and_collects_everything_gets_every_known_loop()
    {
        var snapshot = Snapshot(mode: "collect");
        var desired = ExchangeWorker.DesiredCollectors(snapshot, "kraken-futures", AllImplemented);

        Assert.Equal(ExchangeWorker.KnownCollectorDatasets.OrderBy(c => c), desired.OrderBy(c => c));
    }

    [Fact]
    public void A_dataset_turned_off_by_policy_is_dropped_even_if_the_adapter_implements_it()
    {
        var snapshot = Snapshot(overrides: new() { [("kraken-futures", "funding")] = "disabled" });
        var desired = ExchangeWorker.DesiredCollectors(snapshot, "kraken-futures", AllImplemented);

        Assert.DoesNotContain("funding", desired);
        Assert.Contains("snapshot", desired);
        Assert.Contains("candles", desired);
    }

    [Fact]
    public void A_dataset_the_adapter_never_declared_is_never_started_even_if_policy_says_collect()
    {
        // The fake's own real gap: GetOrderBookAsync always returns null, so it declares no depth
        // capability at all — mirrors IExchangeMarketData.Capabilities on FakeExchangeMarketData.
        var snapshot = Snapshot(mode: "collect");
        var implemented = new[] { "discovery", "snapshot", "candles", "funding" };   // no depth

        var desired = ExchangeWorker.DesiredCollectors(snapshot, "fake", implemented);

        Assert.DoesNotContain("depth", desired);
        Assert.Contains("snapshot", desired);
    }

    [Fact]
    public void On_demand_mode_does_not_start_a_loop_either()
    {
        var snapshot = Snapshot(overrides: new() { [("kraken-futures", "candles")] = "on_demand" });
        var desired = ExchangeWorker.DesiredCollectors(snapshot, "kraken-futures", AllImplemented);

        Assert.DoesNotContain("candles", desired);
    }

    [Fact]
    public void Datasets_without_a_loop_of_their_own_are_never_offered_even_when_policy_collects_them()
    {
        // 'rollup' has no Collector class — it is the service-wide loop under ServiceExchange — and
        // 'spec_versions' deliberately has none either: it is written inside the discovery pass,
        // gated on its own mode there, because a spec version is a fact about a listing.
        //
        // trades/book/open_interest/liquidations/candles_mark/candles_index USED to be listed here
        // as "no implementation anywhere yet". They all have one now, so the assertion that would
        // still hold for them is the one below it: policy alone is not enough, the adapter has to
        // declare the capability too.
        var snapshot = Snapshot(mode: "collect");
        var desired = ExchangeWorker.DesiredCollectors(snapshot, "kraken-futures", AllImplemented);

        Assert.DoesNotContain("rollup", desired);
        Assert.DoesNotContain("spec_versions", desired);
    }

    [Fact]
    public void A_venue_that_lacks_a_series_never_gets_its_loop_however_the_policy_is_set()
    {
        // Kraken publishes no mark- or index-price bars on any transport, so its adapter declares
        // neither capability — and 0041 enables candles_mark/candles_index for Binance only. Even
        // with policy set to collect everywhere, the missing capability is what stops the loop.
        var snapshot = Snapshot(mode: "collect");
        var krakenImplements = AllImplemented.Where(d => d is not ("candles_mark" or "candles_index")).ToArray();

        var desired = ExchangeWorker.DesiredCollectors(snapshot, "kraken-futures", krakenImplements);

        Assert.DoesNotContain("candles_mark", desired);
        Assert.DoesNotContain("candles_index", desired);
        Assert.Contains("trades", desired);
        Assert.Contains("book", desired);
    }

    private static SettingsSnapshot Snapshot(
        string mode = "collect", Dictionary<(string Exchange, string Dataset), string>? overrides = null)
    {
        var datasets = new[]
            {
                "discovery", "snapshot", "depth", "candles", "funding", "rollup",
                "trades", "book", "open_interest", "liquidations", "candles_mark", "candles_index",
                "spec_versions",
            }
            .ToDictionary(
                c => c,
                c => new DatasetDefaults { Code = c, Kind = "feed", DefaultMode = mode, DefaultIntervalS = 60, DefaultRetentionDays = null },
                StringComparer.Ordinal);

        var matrix = new Dictionary<(string, string), SegmentDatasetRow>();
        foreach (var segmentCode in new[] { "kraken-futures", "fake" })
        {
            foreach (var datasetCode in datasets.Keys)
            {
                var cellMode = overrides?.GetValueOrDefault((segmentCode, datasetCode)) ?? mode;
                matrix[(segmentCode, datasetCode)] = new SegmentDatasetRow
                {
                    SegmentCode = segmentCode,
                    DatasetCode = datasetCode,
                    Mode = cellMode,
                    IntervalS = null,
                    RetentionDays = null,
                    Transport = null,
                };
            }
        }

        return new SettingsSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],   // venues: nothing here reads a request budget
            [],
            datasets,
            new Dictionary<(string, string), string>(),
            matrix);
    }
}

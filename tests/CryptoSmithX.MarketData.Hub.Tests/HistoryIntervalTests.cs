namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The keep-interval cascade introduced in 0020: segment_dataset.history_interval_s ->
/// dataset.default_history_interval_s -> the poll interval, floored at the poll interval.
///
/// The floor is the part worth pinning. Every consumer of this number — the collector's bucket, the
/// hourly expected_count, the "kept every N s" line on the market-state page — must agree on what
/// the rate actually is, and a configured value faster than the poll rate is not achievable by any
/// of them. If they disagree, the one metric that separates "we did not watch this hour" from "the
/// market was quiet" starts lying, which is the failure this whole page exists to prevent.
/// </summary>
public sealed class HistoryIntervalTests
{
    [Fact]
    public void Cell_value_wins_over_the_dataset_default()
    {
        var s = Snapshot(pollS: 10, datasetHistoryS: 60, cellHistoryS: 30);
        Assert.Equal(TimeSpan.FromSeconds(30), s.HistoryInterval("kraken-futures", "snapshot"));
    }

    [Fact]
    public void Dataset_default_applies_when_the_cell_says_nothing()
    {
        var s = Snapshot(pollS: 10, datasetHistoryS: 60, cellHistoryS: null);
        Assert.Equal(TimeSpan.FromSeconds(60), s.HistoryInterval("kraken-futures", "snapshot"));
    }

    [Fact]
    public void A_dataset_with_no_default_keeps_every_pass()
    {
        // No default means the dataset does not distinguish asking from keeping — candles and
        // funding write every pass whole. The keep rate is then the poll rate, not "unset".
        var s = Snapshot(pollS: 60, datasetHistoryS: null, cellHistoryS: null);
        Assert.Equal(TimeSpan.FromSeconds(60), s.HistoryInterval("kraken-futures", "snapshot"));
    }

    [Fact]
    public void Keeping_never_outruns_asking()
    {
        // 5 s of keeping against a 10 s poll is 10 s: the collector cannot write a row for an
        // observation it never made, so reporting 5 would overstate what exists.
        var s = Snapshot(pollS: 10, datasetHistoryS: 60, cellHistoryS: 5);
        Assert.Equal(TimeSpan.FromSeconds(10), s.HistoryInterval("kraken-futures", "snapshot"));
    }

    [Fact]
    public void Equal_rates_mean_nothing_is_dropped()
    {
        var s = Snapshot(pollS: 10, datasetHistoryS: 10, cellHistoryS: null);
        Assert.Equal(s.DatasetInterval("kraken-futures", "snapshot"), s.HistoryInterval("kraken-futures", "snapshot"));
    }

    [Fact]
    public void Two_segments_resolve_independently()
    {
        // The reason the knob moved at all: 14 500 spot instruments and 1 500 perps should not be
        // forced onto one rate by a single global row.
        var s = Snapshot(pollS: 10, datasetHistoryS: 60, cellHistoryS: null,
                         otherSegmentHistoryS: 10);
        Assert.Equal(TimeSpan.FromSeconds(60), s.HistoryInterval("kraken-futures", "snapshot"));
        Assert.Equal(TimeSpan.FromSeconds(10), s.HistoryInterval("fake", "snapshot"));
    }

    private static SettingsSnapshot Snapshot(
        int pollS, int? datasetHistoryS, int? cellHistoryS, int? otherSegmentHistoryS = null)
    {
        var datasets = new Dictionary<string, DatasetDefaults>(StringComparer.Ordinal)
        {
            ["snapshot"] = new()
            {
                Code = "snapshot",
                Kind = "feed",
                DefaultMode = "collect",
                DefaultIntervalS = pollS,
                DefaultHistoryIntervalS = datasetHistoryS,
                DefaultRetentionDays = null,
            },
        };

        var matrix = new Dictionary<(string, string), SegmentDatasetRow>
        {
            [("kraken-futures", "snapshot")] = new()
            {
                SegmentCode = "kraken-futures",
                DatasetCode = "snapshot",
                Mode = "collect",
                HistoryIntervalS = cellHistoryS,
            },
            [("fake", "snapshot")] = new()
            {
                SegmentCode = "fake",
                DatasetCode = "snapshot",
                Mode = "collect",
                HistoryIntervalS = otherSegmentHistoryS,
            },
        };

        return new SettingsSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],   // venues: nothing here reads a request budget
            [],
            datasets,
            new Dictionary<(string, string), string>(),
            matrix);
    }
}

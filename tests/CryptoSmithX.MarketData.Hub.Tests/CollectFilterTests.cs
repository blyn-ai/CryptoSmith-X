using CryptoSmithX.MarketData.Hub.Ingestion;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The data collectors must honour collect=false (0008): an operator can turn a listing off and its
/// snapshots, candles, depth and funding stop, while discovery keeps seeing it. The collectors
/// filter in SQL, so this guards the one thing a refactor could silently drop — the collect clause
/// in each target query. Runtime behaviour is proven on the live stack; this keeps CI honest without
/// a database. Discovery is deliberately absent: it never filters on collect.
/// </summary>
public sealed class CollectFilterTests
{
    [Fact]
    public void Snapshot_targets_only_collected_instruments() =>
        Assert.Contains("collect = true", SnapshotCollector.TargetInstrumentsSql);

    [Fact]
    public void Depth_targets_only_collected_trading_instruments()
    {
        Assert.Contains("collect = true", DepthCollector.TargetInstrumentsSql);
        Assert.Contains("status = 'trading'", DepthCollector.TargetInstrumentsSql);
    }

    [Fact]
    public void Candles_target_only_collected_instruments() =>
        Assert.Contains("i.collect = true", CandleCollector.TargetInstrumentsSql);

    [Fact]
    public void Funding_targets_only_collected_instruments() =>
        Assert.Contains("i.collect = true", FundingCollector.TargetInstrumentsSql);
}

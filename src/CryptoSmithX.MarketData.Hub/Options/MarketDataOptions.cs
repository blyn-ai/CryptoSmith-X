namespace CryptoSmithX.MarketData.Hub.Options;

/// <summary>The whole configuration surface of the service.</summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    public List<ExchangeOptions> Exchanges { get; init; } = [];
    public int SnapshotIntervalSeconds { get; init; } = 10;
    public int CandleIntervalSeconds { get; init; } = 60;
    public int DiscoveryIntervalMinutes { get; init; } = 60;
    public int FundingIntervalMinutes { get; init; } = 60;

    /// <summary>Minutes. 1 is collected from the venue and is never listed here.</summary>
    public List<int> DerivedTimeframes { get; init; } = [5, 15, 60, 240, 720, 1440];

    public int SnapshotRetentionDays { get; init; } = 90;

    /// <summary>How far back candles are fetched the first time an instrument is seen.</summary>
    public int CandleBackfillHours { get; init; } = 3;

    /// <summary>How far back funding history is fetched the first time an instrument is seen.</summary>
    public int FundingBackfillHours { get; init; } = 168;

    /// <summary>Consecutive discovery rounds an instrument may be missing before it is delisted.</summary>
    public int DelistAfterMissedDiscoveries { get; init; } = 3;

    public TimeSpan SnapshotInterval => TimeSpan.FromSeconds(SnapshotIntervalSeconds);
    public TimeSpan CandleInterval => TimeSpan.FromSeconds(CandleIntervalSeconds);
    public TimeSpan DiscoveryInterval => TimeSpan.FromMinutes(DiscoveryIntervalMinutes);
    public TimeSpan FundingInterval => TimeSpan.FromMinutes(FundingIntervalMinutes);
}

public sealed class ExchangeOptions
{
    /// <summary>Matches <c>exchange.code</c>.</summary>
    public string Code { get; init; } = "";

    public bool Enabled { get; init; } = true;

    /// <summary>Which adapter implementation to construct. Only "fake" exists today.</summary>
    public string Adapter { get; init; } = "fake";

    public List<string> QuoteAssets { get; init; } = ["USD", "USDT", "USDC"];
    public List<string> Blacklist { get; init; } = [];
}

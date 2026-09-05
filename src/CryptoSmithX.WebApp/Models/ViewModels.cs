namespace CryptoSmithX.WebApp.Models;

// timestamptz comes back from Npgsql as DateTime with Kind=Utc; that is what these say.

public sealed record TenantRow(string Code, string Name, DateTime CreatedAt);

public sealed record BotListItem(
    int Id,
    string TenantCode,
    string BotInstanceId,
    string Name,
    bool IsEnabled,
    DateTime? LastHeartbeatAt,
    double? HeartbeatAgeSeconds,
    bool HasToken);

public sealed record BotEventRow(
    string EventId,
    DateTime Utc,
    string Type,
    string Payload,
    DateTime ReceivedAt);

public sealed record BotDetails(
    int Id,
    string TenantCode,
    string BotInstanceId,
    string Name,
    bool IsEnabled,
    DateTime? LastHeartbeatAt,
    string? LastHeartbeatJson,
    int PolicyVersion,
    string? PolicyJson,
    IReadOnlyList<BotEventRow> Events);

/// <summary>The one-time token banner shown right after a bot is created or its token regenerated.</summary>
public sealed record NewTokenNotice(string BotInstanceId, string Token);

/// <summary>
/// One trading surface — a segment, not a company. <paramref name="Code"/> is the segment code
/// (<c>kraken-futures</c>); <paramref name="ExchangeCode"/> is the venue it belongs to
/// (<c>kraken</c>). Two segments of one venue share an account, keys and a per-IP request budget,
/// which is the whole reason the levels are separate rows.
/// </summary>
public sealed record ExchangeListItem(
    string Code,
    string Name,
    string Status,
    string? Description,
    string ExchangeCode,
    string ExchangeName,
    string Kind,
    int TradingInstruments,
    int KnownInstruments,
    int? MaxFailures,
    double? AvgDurationMs,
    double? DiscoveryAgeSeconds);

public sealed record ExchangeCollectorRow(
    string Collector,
    double? LastSuccessAgeSeconds,
    int ConsecutiveFailures,
    int? InstrumentsExpected,
    int? LastDurationMs,
    double? AvgDurationMs,
    string? LastError,
    double? LastErrorAgeSeconds);

public sealed record StaleInstrument(int Id, string Symbol, double? AgeSeconds);

/// <summary>
/// The editable configuration of one exchange. Interval overrides are null when the global wins.
/// Property-init so Dapper materialises the text[] columns through setters, not the constructor.
/// </summary>
public sealed record ExchangeConfigRow
{
    public string Adapter { get; init; } = "";
    public string? BaseUrl { get; init; }
    public string? ChartsUrl { get; init; }
    public string? WsUrl { get; init; }
    public string[] QuoteAssets { get; init; } = [];
    public string[] Blacklist { get; init; } = [];
    public int? SnapshotIntervalS { get; init; }
    public int? CandleIntervalS { get; init; }
    public int? DiscoveryIntervalMin { get; init; }
    public int? FundingIntervalMin { get; init; }
    public int? DepthIntervalS { get; init; }
    public string? UpdatedBy { get; init; }
}

/// <summary>The editable configuration of an exchange (everything except status, which is guarded).</summary>
public sealed record ExchangeSaveInput(
    string Code,
    string Name,
    string? Description,
    string? BaseUrl,
    string? ChartsUrl,
    string? WsUrl,
    string[] QuoteAssets,
    string[] Blacklist,
    int? SnapshotIntervalS,
    int? CandleIntervalS,
    int? DiscoveryIntervalMin,
    int? FundingIntervalMin,
    int? DepthIntervalS,
    string? UpdatedBy);

/// <summary>One global market-data setting, for the System → Settings page.</summary>
public sealed record SettingRow(
    string Key, string Value, string Kind, string Description, DateTime UpdatedAt, string? UpdatedBy);

public sealed record ExchangeDetails(
    ExchangeListItem Exchange,
    ExchangeConfigRow Config,
    IReadOnlyDictionary<string, int> GlobalIntervals,
    IReadOnlyList<ExchangeCollectorRow> Collectors,
    IReadOnlyList<StaleInstrument> Stalest,
    IReadOnlyList<double> Throughput,
    IReadOnlyList<LatencySeries> Latency,
    IReadOnlyList<FeedRow> Feeds,
    IReadOnlyList<FeedDetails> FeedDialogs,
    IReadOnlyList<CollectorGapRow> Gaps);

/// <summary>
/// An interval this venue was not observed for. The point of showing these is that a hole in the
/// data and a quiet market look identical on every chart in this console; only this row says which
/// one it was.
/// </summary>
public sealed record CollectorGapRow(
    string Collector,
    DateTime GapStart,
    DateTime? GapEnd,
    string Cause,
    string? Detail,
    double? SecondsLong);

// ── Data feeds (datasets, 0014 phase 2) ──────────────────────────────────

/// <summary>One row of the "Data feeds" panel: one dataset for one segment, all three axes —
/// capability (fact), policy (decided) and health (observed) — kept visually and structurally
/// separate, per plans/design/data-feeds/HANDOFF.md.</summary>
public sealed record FeedRow(
    string DatasetCode,
    string DatasetName,
    string Kind,
    short SortOrder,
    bool? VenueSupports,
    bool? WeImplement,
    string? HistoryDepth,
    string? HistorySource,
    string Mode,
    string? Transport,
    int? EffectiveIntervalS,
    /// <summary>How often this feed's measurements reach permanent storage. Equals the poll rate for
    /// most feeds, but snapshot and depth share one keep pass — depth writes only the latest row and
    /// is archived by the snapshot collector copying it — so both carry the segment's snapshot keep
    /// rate here (0020).</summary>
    int? ArchiveIntervalS,
    int? EffectiveRetentionDays,
    string? Note,
    double? LastSuccessAgeSeconds,
    int ConsecutiveFailures,
    int? LastDurationMs,
    double? AvgDurationMs);

/// <summary>One capability_key row in the Edit feed dialog's read-only left column — a value with
/// no source is an opinion, so <see cref="Source"/>/<see cref="FilledBy"/>/<see cref="FilledAt"/> are
/// shown with equal weight to the value itself.</summary>
public sealed record FeedCapabilityRow(
    string Key, string Label, string Kind, bool LossRelevant,
    string? Value, string? Source, string? FilledBy, DateTime? FilledAt);

/// <summary>Everything the Edit feed dialog needs for one dataset: the read-only capability
/// column and the editable policy column, including the own/dataset/global cascade for interval
/// and retention.</summary>
public sealed record FeedDetails(
    string DatasetCode,
    string DatasetName,
    string DatasetDescription,
    string Kind,
    IReadOnlyList<FeedCapabilityRow> Capabilities,
    string Mode,
    bool WeImplement,
    int? OwnIntervalS,
    int? DatasetDefaultIntervalS,
    int? OwnHistoryIntervalS,
    int? DatasetDefaultHistoryIntervalS,
    int? ArchiveIntervalS,
    int? OwnRetentionDays,
    int? DatasetDefaultRetentionDays,
    string? Transport,
    IReadOnlyList<string> TransportOptions,
    string? Note,
    string? UpdatedBy,
    DateTime? UpdatedAt,
    string? LatestCapabilityLogLine);

/// <summary>The editable policy of one segment×dataset cell.</summary>
public sealed record FeedSaveInput(
    string SegmentCode,
    string DatasetCode,
    string Mode,
    int? IntervalS,
    int? HistoryIntervalS,
    int? RetentionDays,
    string? Transport,
    string? Note,
    string? ConfirmCode,
    string? UpdatedBy);

// ── Datasets catalogue (screen 3) ────────────────────────────────────────

public sealed record DatasetCard(
    string Code, string Name, string Description, string Kind, short SortOrder,
    string DefaultMode, int? DefaultIntervalS, int? DefaultRetentionDays,
    IReadOnlyList<DatasetVenueRow> Venues);

public sealed record DatasetVenueRow(string DatasetCode, string SegmentCode, string ExchangeName, string Mode, string? Note);

// ── Assets ────────────────────────────────────────────────────────────────
public sealed record AssetListItem(
    string Code,
    string? Name,
    int ListingCount,
    string? ListingsSummary,          // "kraken-futures 1 · fake 1"
    double? OpenInterestNotional,     // sum over listings of open_interest * mark
    double? WorstSnapshotAgeSeconds); // oldest snapshot among the listings

/// <summary>One listing of an asset on one exchange — the row that makes the Asset page a comparison.</summary>
public sealed record AssetListing(
    int InstrumentId,
    string SegmentCode,
    string Symbol,
    string Status,
    bool Collect,
    double? LastPrice,
    double? FundingRate,
    double? OpenInterestNotional,
    double? SpreadBps,
    double? Depth25Notional,          // bid + ask within 25 bps
    double? SnapshotAgeSeconds);

/// <summary>
/// One venue's snapshot measurements for this asset at the requested instant. Every value carries
/// its own lag because every venue is on its own clock: since 0020 the keep interval is per segment,
/// so two venues' newest-at-or-before-T rows are systematically different distances behind T. That
/// is why nothing here is averaged or differenced across venues — see <see cref="AssetVenueBar"/>,
/// which is the measurement that IS comparable.
/// </summary>
public sealed record AssetVenueMeasurement(
    int InstrumentId,
    string SegmentCode,
    string ExchangeCode,
    string Symbol,
    string Status,
    bool Collect,
    DateTime? ReceivedAt,
    double? PriceLagSeconds,
    double? LastPrice,
    double? SpreadBps,
    double? BidSize,
    double? AskSize,
    double? OpenInterest,
    double? OpenInterestLagSeconds,
    double? FundingRate,
    double? Depth25Notional,
    DateTime? DepthAt,
    double? DepthLagSeconds);

/// <summary>
/// One venue's bar covering the requested instant. This is the only measurement in the system taken
/// over the SAME wall-clock window on every venue, which is what makes closes comparable across them
/// at all. <paramref name="BarCount"/> below the timeframe means the bar is incomplete — a venue that
/// contributed four minutes of a five-minute bar must not be read as agreeing or disagreeing with one
/// that contributed five.
/// </summary>
public sealed record AssetVenueBar(
    string SegmentCode,
    string Symbol,
    string QuoteAsset,
    decimal ContractMultiplier,
    DateTime OpenTime,
    short Timeframe,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    int? TradeCount,
    short BarCount)
{
    /// <summary>Every minute the window claims to cover is present. Always true at 1m, where
    /// bar_count is 1 by definition — the check only carries information from 5m up.</summary>
    public bool Complete => BarCount >= Timeframe;

    /// <summary>Something actually traded. A zero-volume bar's close is a carried price, not a
    /// print: 13% of one venue's 1m bars on the dev database are zero-volume. Comparing a carried
    /// price against a real one and calling the difference a divergence is the same class of lie as
    /// comparing two prices measured a minute apart.</summary>
    public bool Traded => Volume > 0;

    /// <summary>The contract terms a comparison has to match on. Two venues quoting the same asset
    /// against different quote currencies, or in contracts of different size, are not quoting the
    /// same number — and this system refuses rather than guessing a conversion it was never told.</summary>
    public (string Quote, decimal Multiplier) Terms => (QuoteAsset, ContractMultiplier);
}

/// <summary>Closes for the bars leading up to the instant, one series per venue, for the sparkline.</summary>
public sealed record AssetVenueSeries(string SegmentCode, IReadOnlyList<double> Closes);

/// <summary>
/// The asset across every venue that lists it, at one instant. Two panels on purpose: bars, which
/// share a wall clock and can therefore be compared to each other, and snapshot measurements, which
/// do not and therefore are only ever shown side by side with their own lags.
/// </summary>
public sealed record AssetAtInstant(
    string Code,
    string? Name,
    DateTime At,
    short Timeframe,
    DateTime AnchorOpen,
    IReadOnlyList<AssetVenueMeasurement> Venues,
    IReadOnlyList<AssetVenueBar> Bars,
    IReadOnlyList<AssetVenueSeries> Series,
    DateTime? EarliestStored)
{
    /// <summary>When the compared window ended. It is at or before the requested instant, never
    /// after: a bar that is still open contains trades that had not happened yet at T.</summary>
    public DateTime AnchorClose => AnchorOpen.AddMinutes(Timeframe);

    /// <summary>
    /// The bars a claim may be built on: complete, actually traded, and on matching contract terms
    /// with at least one other venue. Everything else stays on the page — it is data — but is not
    /// allowed into the verdict.
    /// </summary>
    /// Eligibility, not sufficiency: a lone eligible bar belongs here so the page can say "only one
    /// venue could be compared" rather than listing it as held out for a reason that is not true of
    /// it. Whether a CLAIM can be made is a separate question, and needs two.
    public IReadOnlyList<AssetVenueBar> Comparable =>
        Bars.Where(b => b.Complete && b.Traded)
            .GroupBy(b => b.Terms)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Quote, StringComparer.Ordinal)
            .FirstOrDefault()?.ToList()
        ?? [];

    /// <summary>Bars held out of the verdict, and why — stated rather than silently dropped.</summary>
    public IReadOnlyList<(AssetVenueBar Bar, string Reason)> Excluded =>
        Bars.Where(b => !Comparable.Contains(b))
            .Select(b => (b, !b.Complete ? $"covers {b.BarCount} of {b.Timeframe} minutes"
                           : !b.Traded ? "no volume — the close is a carried price"
                           : $"quoted in {b.QuoteAsset}"
                             + (b.ContractMultiplier == 1 ? "" : $" × {b.ContractMultiplier}")))
            .ToList();

    /// <summary>
    /// The one cross-venue claim this data supports without a timing assumption. Both venues'
    /// [low, high] cover the SAME closed wall-clock window, so if those ranges do not intersect
    /// there was no price at which both traded during it — and no polling difference, keep-interval
    /// difference or sweep width can manufacture that. Null when the ranges overlap, which is the
    /// ordinary case and means no divergence can be asserted at all.
    /// </summary>
    public double? DisjointGap
    {
        get
        {
            var c = Comparable;
            if (c.Count < 2) { return null; }
            double maxLow = c.Max(b => b.Low), minHigh = c.Min(b => b.High);
            return maxLow > minHigh ? maxLow - minHigh : null;
        }
    }

    /// <summary>Close-to-close difference across the comparable bars. An illustration, not a claim:
    /// two venues can close a minute apart on the last trade of that minute and still have traded at
    /// the same prices throughout it, which is what <see cref="DisjointGap"/> tests properly.</summary>
    public double? CloseSpreadBps
    {
        get
        {
            var c = Comparable;
            if (c.Count < 2) { return null; }
            double lo = c.Min(b => b.Close), hi = c.Max(b => b.Close);
            var mid = (lo + hi) / 2;
            return mid > 0 ? (hi - lo) / mid * 10000 : null;
        }
    }

    /// <summary>Windows are epoch-aligned, so the last one CLOSED at or before an instant is the
    /// floor of that instant minus one window. At 19:15:30 with 1m that is 19:14:00–19:15:00; at
    /// exactly 19:15:00 it is the same window, which closed precisely then.</summary>
    public static DateTime Anchor(DateTime at, short timeframe)
    {
        var window = TimeSpan.FromMinutes(timeframe).Ticks;
        return new DateTime(at.Ticks / window * window, DateTimeKind.Utc).AddMinutes(-timeframe);
    }
}

public sealed record AssetAliasRow(string? SegmentCode, string Alias, string AssetCode, decimal Multiplier, string? Note);

public sealed record AssetDetails(
    string Code,
    string? Name,
    string? Note,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    IReadOnlyList<AssetListing> Listings,
    IReadOnlyList<AssetAliasRow> Aliases);

// ── Instruments ───────────────────────────────────────────────────────────
public sealed record InstrumentListItem(
    int Id,
    string SegmentCode,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Status,
    bool Collect,
    double? LastPrice,
    double? FundingRate,
    double? OpenInterestNotional,
    double? SnapshotAgeSeconds);

/// <summary>One page of the instrument list plus the filter/sort state, so the view can build links.</summary>
public sealed record InstrumentPage(
    IReadOnlyList<InstrumentListItem> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> Segments,
    string? Segment,
    string? Status,
    bool OnlyTrading,
    string? Search,
    string Sort)
{
    public int From => Total == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int To => Math.Min(Page * PageSize, Total);
    public int Pages => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>The current market snapshot for one instrument, with a separate age per data layer.</summary>
public sealed record SnapshotView(
    DateTime ReceivedAt,
    double? SnapshotAgeSeconds,
    double LastPrice,
    double BidPrice,
    double AskPrice,
    double BidSize,
    double AskSize,
    double? SpreadBps,
    double MarkPrice,
    double IndexPrice,
    double FundingRate,
    double Turnover24h,
    double OpenInterest,
    double OpenInterestNotional,
    double? DepthBid10,
    double? DepthAsk10,
    double? DepthBid25,
    double? DepthAsk25,
    double? DepthBid50,
    double? DepthAsk50,
    DateTime? DepthAt,
    double? DepthAgeSeconds);

public sealed record CandlePoint(DateTime OpenTime, double Open, double High, double Low, double Close);

public sealed record MetricPoint(DateTime HourTime, double OpenInterestLast, double FundingRateLast, double? SpreadBpsAvg);

public sealed record FundingRow(DateTime FundingTime, double Rate);

/// <summary>Data-coverage summary for the detail page: 1-minute completeness and the stored ranges.</summary>
public sealed record CoverageView(
    int Minutes24h,
    int Holes24h,
    DateTime? CandleFrom,
    DateTime? CandleTo,
    DateTime? FundingFrom,
    DateTime? FundingTo,
    double? LastCandleAgeSeconds,
    double? LastFundingAgeSeconds,
    bool ExchangeCollecting,
    bool Silent);

public sealed record InstrumentDetails(
    int Id,
    string SegmentCode,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Status,
    DateTime StatusChangedAt,
    DateTime? ListedAt,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    bool Collect,
    string? CollectNote,
    DateTime? CollectChangedAt,
    string? CollectChangedBy,
    short FundingIntervalHours,
    SnapshotView? Snapshot,
    int Timeframe,
    IReadOnlyList<int> Timeframes,
    IReadOnlyList<CandlePoint> Candles,
    IReadOnlyList<MetricPoint> Metrics,
    IReadOnlyList<FundingRow> Funding,
    CoverageView Coverage,
    IReadOnlyList<SiblingListing> Siblings);

/// <summary>Another venue's listing of the same canonical asset — the hop between exchanges.</summary>
public sealed record SiblingListing(int Id, string SegmentCode, string Symbol);

// ── Admin dashboard ───────────────────────────────────────────────────────
public sealed record DashExchange(
    string Code, string Name, string Status, string Health,
    int TradingInstruments, int KnownInstruments, double? WorstAgeSeconds,
    IReadOnlyList<double> Spark);

public sealed record DashCollector(
    string SegmentCode, string Collector, double? LastSuccessAgeSeconds,
    int ConsecutiveFailures, int? AvgDurationMs, string? LastError, string Health);

public sealed record DashBot(
    int Id,
    string TenantCode, string BotInstanceId, double? LastHeartbeatAgeSeconds, bool Online);

public sealed record DashEvent(DateTime Utc, string Type, string TenantCode, int BotId, string BotInstanceId, bool IsError);

public sealed record DashTenant(string Code, int BotCount, DateTime CreatedAt);

public sealed record Dashboard(
    int ExchangesEnabled, int ExchangesTotal, int ExchangesMaintenance, int ExchangesPlanned,
    int CollectorsOk, int CollectorsTotal, int CollectorsFailing,
    int InstrumentsTrading, int InstrumentsKnown,
    int BotsOnline, int BotsTotal, string? SilentBotNote,
    int EventsLastHour,
    int Failing, int Degraded, string Verdict,
    IReadOnlyList<DashExchange> Exchanges,
    IReadOnlyList<DashCollector> Collectors,
    IReadOnlyList<DashBot> Bots,
    IReadOnlyList<DashEvent> Events,
    IReadOnlyList<DashTenant> Tenants,
    IReadOnlyList<double> IngestBuckets, int IngestPeak,
    DateTime AsOf);

// ── Clients (derived from tenant + bot; no client table yet) ───────────────
public sealed record ClientListItem(
    string Code, string Name, int BotCount, bool Online, double? HeartbeatAgeSeconds, int Events24h);

public sealed record ClientBot(int Id, string BotInstanceId, bool Online, double? HeartbeatAgeSeconds);

public sealed record ClientDetails(
    string Code, string Name, bool Online, double? HeartbeatAgeSeconds,
    int BotsOnline, int BotsTotal, int Events24h,
    IReadOnlyList<ClientBot> Bots);

/// <summary>One row of the header search — kind decides the group, url is the landing page.</summary>
public sealed record SearchHit(string Kind, string Title, string Note, string Url);

// ── collector runs (0009): история прогонов и «что пришло» ────────────────
public sealed record CollectorRunRow(
    long Id, string Collector, DateTime StartedAt, int DurationMs, bool Ok, string? Error, int? Items);

/// <summary>One latency series per collector for the exchange page trend.</summary>
public sealed record LatencySeries(string Collector, IReadOnlyList<double> AvgMs);

public sealed record RunDataRow(string Symbol, string What, DateTime When);

/// <summary>A run plus the data whose timestamps fall inside its window (time-based, not run-id based).</summary>
public sealed record RunDetails(
    string SegmentCode, CollectorRunRow Run,
    string Caption, int Total, IReadOnlyList<RunDataRow> Rows, string EmptyNote,
    int? PollSeconds, int? KeepSeconds);

// ── Market state at a moment ────────────────────────────────────────────────

/// <summary>
/// One instrument as it was at the requested instant. Three measurement times, not one: price, open
/// interest and depth each arrive on their own clock, and collapsing them into a single "age" is the
/// lie this page exists to prevent.
/// </summary>
public sealed record MarketStateRow(
    int InstrumentId,
    string SegmentCode,
    string Symbol,
    string? BaseAsset,
    string? QuoteAsset,
    string Status,
    DateTime FirstSeenAt,
    DateTime? ReceivedAt,
    double? PriceLagSeconds,
    double? LastPrice,
    double? BidPrice,
    double? AskPrice,
    double? SpreadBps,
    double? BidSize,
    double? AskSize,
    double? MarkPrice,
    double? FundingRate,
    double? Turnover24h,
    double? OpenInterest,
    double? OpenInterestLagSeconds,
    DateTime? DepthAt,
    double? DepthLagSeconds,
    double? DepthBid25,
    double? DepthAsk25)
{
    /// <summary>
    /// Why a row has no numbers, from a closed vocabulary. "Absent" must never read as "zero", and
    /// "we were not looking" must never read as "the market was quiet" — so the reason is a column,
    /// not an inference the reader is left to make from a blank cell.
    /// </summary>
    public string StateAt(DateTime at) =>
        ReceivedAt is not null ? "observed"
        : FirstSeenAt > at ? "not listed yet"
        : "no observation";
}

public sealed record MarketStateSlice(
    DateTime At,
    string? Segment,
    IReadOnlyList<MarketStateRow> Rows,
    IReadOnlyList<CollectorGapRow> GapsCoveringT,
    DateTime? EarliestStored,
    /// <summary>Cadence per segment code (0020) — never one number for the page, because two venues
    /// in scope can legitimately poll and keep at different rates.</summary>
    IReadOnlyDictionary<string, SegmentCadence> Cadence,
    IReadOnlyList<string> Segments);

/// <summary>
/// What one segment's clocks actually are. Three separate numbers on purpose: a price row is stale
/// against the keep rate, "are we dropping anything" is keep against poll, and depth is neither —
/// its sweep across a large venue is minutes wide, so it is judged against its own interval plus the
/// sweep duration we have actually measured.
/// </summary>
public sealed record SegmentCadence(
    string SegmentCode,
    int? PollSeconds,
    int? KeepSeconds,
    int? DepthPollSeconds,
    double? DepthSweepSeconds)
{
    /// <summary>Two keep intervals: beyond that the row is the last thing seen, not the market at T.</summary>
    public double PriceTolerance => (KeepSeconds ?? 60) * 2.0;

    /// <summary>
    /// Depth is legitimately much older than the price beside it: the loop runs on its own interval
    /// and one pass takes as long as it takes to walk every instrument on the venue. Both are
    /// counted, and the measured sweep is used rather than assumed — an unmeasured venue falls back
    /// to the interval alone rather than to the snapshot clock, which is not depth's clock at all.
    /// </summary>
    public double DepthTolerance => ((DepthPollSeconds ?? KeepSeconds ?? 60) * 2.0) + (DepthSweepSeconds ?? 0);

    /// <summary>True when this segment stores less than it observes — the only condition under which
    /// the loss warning is honest. The old gate compared the keep rate against a literal 10, which
    /// was the snapshot poll default when it was written: it fired on a segment polling and keeping
    /// at 60 (nothing dropped) and stayed silent on one polling at 1 and keeping at 10 (nine in ten
    /// discarded).</summary>
    public bool Drops => KeepSeconds is { } k && PollSeconds is { } p && k > p;
}

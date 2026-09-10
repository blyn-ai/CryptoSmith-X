namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// One row of the venue strip (`/studio/` plan item A1): a segment, as catalogued — not as observed.
/// Everything here comes from <c>exchange</c>/<c>segment</c>, never from a snapshot, because the
/// strip answers "what are we watching" rather than "what is it saying right now" — that second
/// question is the pair board underneath it.
/// </summary>
/// <param name="Status">planned / enabled / disabled / maintenance / abandoned — the segment's own
/// CHECK values, printed verbatim. A planned segment renders here dimmed, on purpose: the strip's
/// whole point is to answer "what are we watching for", and an unlaunched segment is part of that
/// answer, not noise to filter out.</param>
/// <param name="BlacklistCount">How many symbols this segment excludes before discovery ever writes
/// a row. Counted, never listed — a live blacklist is an operational detail, not a public catalogue,
/// and enumerating it would invite reading it as a claim about what those symbols are rather than a
/// fact about what this segment declines to watch.</param>
public sealed record VenueStripRow(
    string ExchangeCode,
    string ExchangeName,
    string SegmentCode,
    string Kind,
    string Adapter,
    string Status,
    // string[], not IReadOnlyList<string>: Dapper needs the exact array type a positional record
    // reads back for a Postgres text[] column, per this project's own documented gotcha.
    string[] QuoteAssets,
    int BlacklistCount,
    int RequestBudgetPerS,
    int MaxConcurrentRequests,
    string RequestBudgetSource);

/// <summary>One dataset's row in a segment's collection matrix (A2, block 1). The matrix is always
/// complete — every segment has a row for every dataset (0014) — so a dataset that never appears
/// here would be a bug in the query, not a segment that has not decided yet.</summary>
public sealed record DatasetModeRow(
    string DatasetCode,
    string Mode,
    int? IntervalSeconds,
    int? HistoryIntervalSeconds,
    int? RetentionDays);

/// <summary>One collector's current state (A2, block 2) — what <c>collector_status</c> holds, one
/// row per (segment, collector) that has ever run at least once. A dataset in the matrix above with
/// no matching row here has never run, which is itself the fact: it renders as "never run", not as
/// a zero or a dash standing in for one.</summary>
public sealed record CollectorStateRow(
    string Collector,
    DateTime? LastAttemptAt,
    DateTime? LastSuccessAt,
    int ConsecutiveFailures,
    double? AvgDurationMs,
    DateTime? WatermarkAt);

/// <summary>One pass, from <c>collector_run</c> (A2, block 3) — the only source for the cadence a
/// segment is ACTUALLY polled at, as opposed to the <c>interval_s</c> it is configured for.</summary>
public sealed record CollectorRunRow(
    string Collector,
    DateTime StartedAt,
    int DurationMs,
    bool Ok,
    string? Error,
    int? Items,
    string? Transport,
    int? HttpStatus);

/// <summary>Everything A2's disclosure needs for one segment, fetched together because the
/// disclosure opens all three blocks at once.</summary>
public sealed record VenueDetail(
    IReadOnlyList<DatasetModeRow> Modes,
    IReadOnlyList<CollectorStateRow> Collectors,
    IReadOnlyList<CollectorRunRow> Runs);

/// <summary>One raw spelling that resolves into this asset (A3) — the row <c>asset_alias</c> holds,
/// not the canonical code the board already prints. <c>ExchangeCode</c> is null for a global alias
/// (an alias that resolves the same way on every venue); the view prints that as "all venues", never
/// as a blank.</summary>
public sealed record AssetAliasRow(
    string? ExchangeCode,
    string Alias,
    string AssetCode,
    double Multiplier,
    string? Note);

/// <summary>One instrument's collect decision (A4) — the audit trail behind a single listing.
/// <c>collect</c> is written only on INSERT (a re-listing keeps whatever it had); a discovery pass
/// never revisits it, which is why <see cref="ChangedAt"/> and <see cref="ChangedBy"/> can both be
/// null on a listing that has been collecting since the day it first appeared — nobody ever had to
/// decide, the gate decided once, silently, at insert time.</summary>
public sealed record CollectAuditRow(
    string SegmentCode,
    string Symbol,
    string Status,
    bool Collect,
    string? Note,
    DateTime? ChangedAt,
    string? ChangedBy);

/// <summary>What one asset family's card-disclosure needs (A3 + A4 together, one panel): the raw
/// spellings that fold into it, and the collect decision on every listing — most useful exactly
/// where a listing is not collecting, but printed for all of them, since a card is not the place to
/// silently drop the ones that already decided "yes".</summary>
public sealed record AssetRegistryDetail(
    IReadOnlyList<AssetAliasRow> Aliases,
    bool? AutoCollect,
    IReadOnlyList<CollectAuditRow> Listings);

/// <summary>The three counts behind A4's rewritten footer sentence — INSTRUMENTS, not families,
/// because <c>collect</c> is a fact about a listing and folding it into a family average would blur
/// the one thing this sentence exists to keep sharp.</summary>
public sealed record CollectCounts(int Collecting, int Waiting, int BlacklistedSymbols);

using System.Data.Common;
using CryptoSmithX.WebApp.Studio.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Studio.Data;

/// <summary>
/// The venue strip and its disclosure (plan item A1/A2): the axis the pair board never had. Where
/// <see cref="StudioStore"/> answers "what is a pair worth right now", this answers "what are we
/// watching, and how hard" — catalogue and collection health, never a snapshot.
///
/// Three tables read here — <c>segment_dataset</c>, <c>collector_status</c>, <c>collector_run</c> —
/// and the last two needed a new grant (0043): <c>studio_reader</c> withheld them from 0025 onward
/// on the argument that the public pages derive freshness from the data's own age, never from a
/// collector's state table. That argument holds for a PAIR's freshness; it does not hold for "how
/// often do we poll this venue", which has no other honest source.
/// </summary>
public static class VenueStripStore
{
    /// <summary>
    /// One row per segment, every status included. A planned segment carries no adapter that has
    /// ever run and no budget that has ever been measured — its budget columns are `assumed`
    /// placeholders (0043's own seed), printed as such rather than as a real figure.
    /// </summary>
    public const string StripSql =
        """
        select x.code                        as "ExchangeCode",
               x.name                        as "ExchangeName",
               sg.code                       as "SegmentCode",
               sg.kind                       as "Kind",
               sg.adapter                    as "Adapter",
               sg.status                     as "Status",
               sg.quote_assets               as "QuoteAssets",
               cardinality(sg.blacklist)     as "BlacklistCount",
               x.request_budget_per_s        as "RequestBudgetPerS",
               x.max_concurrent_requests     as "MaxConcurrentRequests",
               x.request_budget_source       as "RequestBudgetSource"
          from segment sg
          join exchange x on x.code = sg.exchange_code
         -- The in-process dev exchange, excluded the same way StudioStore excludes it everywhere
         -- else on this site — never shown, on any page, to an anonymous caller.
         where x.code <> 'fake'
         order by (sg.status = 'enabled') desc, (sg.status = 'planned') desc, x.name, sg.code
        """;

    /// <summary>
    /// Read into a tuple, not straight into <see cref="VenueStripRow"/> — found live on test, not in
    /// a review: Dapper's positional-RECORD materializer cannot find a matching constructor when one
    /// of the parameters is an array (<c>string[] QuoteAssets</c>), and throws a generic
    /// "no constructor" error that names nothing about the array. A tuple's own binder does not have
    /// the same limitation — <c>Admin/Data/ExchangeStore.cs</c> already reads <c>quote_assets</c> the
    /// same way for exactly this reason — so the tuple is the query's real return shape and the
    /// record is only assembled after.
    /// </summary>
    public static async Task<IReadOnlyList<VenueStripRow>> ListAsync(DbConnection conn, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(string ExchangeCode, string ExchangeName, string SegmentCode,
                string Kind, string Adapter, string Status, string[] QuoteAssets, int BlacklistCount,
                int RequestBudgetPerS, int MaxConcurrentRequests, string RequestBudgetSource)>(
            new CommandDefinition(StripSql, cancellationToken: ct));

        return rows.Select(r => new VenueStripRow(
            r.ExchangeCode, r.ExchangeName, r.SegmentCode, r.Kind, r.Adapter, r.Status,
            r.QuoteAssets, r.BlacklistCount, r.RequestBudgetPerS, r.MaxConcurrentRequests,
            r.RequestBudgetSource)).ToList();
    }

    /// <summary>
    /// A2 block 1: the collection-mode matrix for one segment. Always <c>dataset</c>'s full row
    /// count (13) — 0014's invariant that the matrix has no "off by absence" state — so the view can
    /// assert that count rather than guess it.
    /// </summary>
    public const string ModesSql =
        """
        select d.code                                                      as "DatasetCode",
               sd.mode                                                     as "Mode",
               coalesce(sd.interval_s, d.default_interval_s)               as "IntervalSeconds",
               coalesce(sd.history_interval_s, d.default_history_interval_s) as "HistoryIntervalSeconds",
               coalesce(sd.retention_days, d.default_retention_days)       as "RetentionDays"
          from dataset d
          join segment_dataset sd on sd.segment_code = @segmentCode and sd.dataset_code = d.code
         order by d.sort_order
        """;

    /// <summary>A2 block 2: one row per collector that has run at least once. A dataset with
    /// <c>mode = 'collect'</c> and no row here has never completed a pass — that absence is the
    /// fact, not a zero standing in for one.</summary>
    public const string CollectorStatusSql =
        """
        select collector             as "Collector",
               last_attempt_at       as "LastAttemptAt",
               last_success_at       as "LastSuccessAt",
               consecutive_failures  as "ConsecutiveFailures",
               avg_duration_ms       as "AvgDurationMs",
               watermark_at          as "WatermarkAt"
          from collector_status
         where segment_code = @segmentCode
         order by collector
        """;

    /// <summary>A2 block 3: the last <paramref name="limit"/> passes for the segment, across every
    /// collector — the only source for the REAL cadence a venue is polled at, as opposed to the
    /// <c>interval_s</c> it is merely configured for.</summary>
    public const string RunsSql =
        """
        select collector    as "Collector",
               started_at   as "StartedAt",
               duration_ms  as "DurationMs",
               ok           as "Ok",
               error        as "Error",
               items        as "Items",
               transport    as "Transport",
               http_status  as "HttpStatus"
          from collector_run
         where segment_code = @segmentCode
         order by started_at desc
         limit @limit
        """;

    /// <summary>How many rows the runs panel shows. Wide enough to span more than one pass of the
    /// slowest collector (discovery, hourly) without paging, small enough that the disclosure opens
    /// instantly.</summary>
    public const int MaxRuns = 60;

    public static async Task<VenueDetail> DetailAsync(DbConnection conn, string segmentCode, CancellationToken ct)
    {
        var modes = (await conn.QueryAsync<DatasetModeRow>(new CommandDefinition(
            ModesSql, new { segmentCode }, cancellationToken: ct))).ToList();

        var collectors = (await conn.QueryAsync<CollectorStateRow>(new CommandDefinition(
            CollectorStatusSql, new { segmentCode }, cancellationToken: ct))).ToList();

        var runs = (await conn.QueryAsync<CollectorRunRow>(new CommandDefinition(
            RunsSql, new { segmentCode, limit = MaxRuns }, cancellationToken: ct))).ToList();

        return new VenueDetail(modes, collectors, runs);
    }
}

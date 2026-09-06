using System.Data.Common;
using CryptoSmithX.Studio.Models;
using Dapper;

namespace CryptoSmithX.Studio.Data;

/// <summary>
/// Where each segment's three windows come from. The window belongs to the CALL, not to the page.
///
/// Half of each window is configuration, read through the cascade
/// <c>segment_dataset.interval_s → dataset.default_interval_s</c>: 10 s for snapshot, 60 s for depth
/// as 0014 seeds them. The other half is the width of one pass, and it is measured, because a call
/// that visits one symbol at a time refreshes an instrument once per pass rather than once per
/// interval. On WEEX one depth pass over 1,005 instruments was measured at 361 s (0021) against a
/// 60 s interval — six passes' worth of difference between what the configuration says and what an
/// individual cell experiences.
///
/// <b>Why the pass is measured from the data and not read from the collector.</b>
/// <c>collector_status.avg_duration_ms</c> holds a sweep duration, and <c>MarketStateStore</c> in the
/// admin console reads exactly that. 0025 does not grant it to <c>studio_reader</c>, deliberately:
/// substituting "the collector wrote something about itself" for "this is how old the data is" is
/// the substitution that produced the Kraken-at-39-seconds incident. So the pass is taken from the
/// timestamps the call itself wrote — how far behind the segment's freshest row the rest of the
/// segment sits.
///
/// <b>Why that cannot hide an outage.</b> This is a dispersion across instruments at one instant,
/// not an age. When a collector stops, every timestamp on the segment freezes together: the
/// dispersion stays exactly where it was while the ages keep climbing, so the cells go stale on
/// schedule. The only case that inflates it is a PARTIAL failure — a venue that stops serving the
/// book for some symbols while the pass keeps cycling — and that is what the percentile and the cap
/// in <see cref="SegmentFreshness.PassCapWindows"/> are for.
///
/// Rejected: a flat 30 s from the design system's prose (stamps △ on every healthy WEEX depth cell —
/// the named bug); one window per page (three calls, three clocks); modelling the pass from
/// <c>exchange.request_budget_per_s</c> (200 req/s on WEEX would model that 361 s pass as 5 s, since
/// the budget is the ceiling we allow, not the rate a sequential pass achieves — a number that looks
/// derived and is wrong by seventy-fold).
/// </summary>
public static class SegmentFreshnessStore
{
    /// <summary>
    /// The fraction of a segment's instruments a pass must have reached before the window is
    /// considered spent.
    ///
    /// Not 1.0. The maximum is the single stalest instrument on the venue, and one symbol whose book
    /// the venue has quietly stopped serving would then set the tolerance for every other cell on
    /// the page — the page would widen its idea of "new" to cover its worst row and never call
    /// anything old again. At 0.95 a segment tolerates one instrument in twenty going dark before
    /// the window moves at all, and <see cref="SegmentFreshness.PassCapWindows"/> catches the case
    /// where more than that do.
    /// </summary>
    public const double PassCoverage = 0.95;

    /// <summary>Public because the guards in it are tested; there is no database in the test run.</summary>
    public const string Sql =
        """
        with live as (
            select i.segment_code,
                   s.received_at,
                   s.open_interest_at,
                   s.depth_at
              from exchange_instrument i
              join segment sg on sg.code = i.segment_code
              join exchange x on x.code  = sg.exchange_code
              join market_snapshot_latest s on s.exchange_instrument_id = i.id
             -- The same population the pages publish, for the same reasons (see StudioStore): an
             -- instrument nobody collects, or a delisted one, would drag the measured pass toward a
             -- call that is not being made.
             where i.collect
               and i.status <> 'delisted'
               and sg.status = 'enabled'
               and x.code <> 'fake'
        ),
        behind as (
            select segment_code,
                   extract(epoch from (max(received_at)      over w - received_at))::double precision      as price_behind_s,
                   extract(epoch from (max(open_interest_at) over w - open_interest_at))::double precision as oi_behind_s,
                   extract(epoch from (max(depth_at)         over w - depth_at))::double precision         as depth_behind_s
              from live
            window w as (partition by segment_code)
        ),
        pass as (
            -- Nulls drop out of the ordered-set aggregate on their own, which is the wanted
            -- behaviour: an instrument whose book has never been measured says nothing about how
            -- long a pass takes.
            select segment_code,
                   percentile_cont(@coverage) within group (order by price_behind_s) as price_pass_s,
                   percentile_cont(@coverage) within group (order by oi_behind_s)    as oi_pass_s,
                   percentile_cont(@coverage) within group (order by depth_behind_s) as depth_pass_s
              from behind
             group by segment_code
        )
        select sg.code as "SegmentCode",
               coalesce(snap.interval_s, (select default_interval_s from dataset where code = 'snapshot'))
                                                                        as "SnapshotIntervalSeconds",
               coalesce(dep.interval_s,  (select default_interval_s from dataset where code = 'depth'))
                                                                        as "DepthIntervalSeconds",
               -- Only when the venue actually runs an open-interest loop of its own. 0014 disables
               -- that dataset on every venue with the note that OI is carried inline in the snapshot
               -- ticker; where that holds, the cadence is the snapshot's and the model says so by
               -- leaving this null rather than by inventing a number for a loop that does not run.
               case when oi.mode = 'collect'
                    then coalesce(oi.interval_s, (select default_interval_s from dataset where code = 'open_interest'))
               end                                                      as "OpenInterestIntervalSeconds",
               p.price_pass_s                                           as "PricePassSeconds",
               p.oi_pass_s                                              as "OpenInterestPassSeconds",
               p.depth_pass_s                                           as "DepthPassSeconds"
          from segment sg
          join exchange x on x.code = sg.exchange_code
          left join segment_dataset snap on snap.segment_code = sg.code and snap.dataset_code = 'snapshot'
          left join segment_dataset dep  on dep.segment_code  = sg.code and dep.dataset_code  = 'depth'
          left join segment_dataset oi   on oi.segment_code   = sg.code and oi.dataset_code   = 'open_interest'
          left join pass p on p.segment_code = sg.code
         where sg.status = 'enabled'
           and x.code <> 'fake'
        """;

    public static async Task<IReadOnlyDictionary<string, SegmentFreshness>> ReadAsync(
        DbConnection conn, CancellationToken ct) =>
        (await conn.QueryAsync<SegmentFreshness>(new CommandDefinition(
            Sql, new { coverage = PassCoverage }, cancellationToken: ct)))
            .ToDictionary(r => r.SegmentCode, StringComparer.Ordinal);
}

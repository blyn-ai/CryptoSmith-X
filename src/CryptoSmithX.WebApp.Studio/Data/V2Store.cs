using System.Data.Common;
using Dapper;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Data;

/// <summary>
/// The four reads the second page needs and the first one never did: the top of each book, the
/// tape, what each dataset actually holds, and where the streams broke.
///
/// All four are keyed on the same instrument ids the page already loaded, and all four run on the
/// one connection inside <see cref="PairPageLoader"/>'s cached payload — a page that asks the
/// database eight times to draw one screen is a page that disagrees with itself between the
/// second question and the seventh.
///
/// EVERY ONE OF THEM CAN COME BACK EMPTY, and empty is a real answer with a real cause: a venue
/// that publishes no level book, a dataset switched off, a stream that has never broken. The
/// bands say which of those it is in words rather than drawing an empty frame.
/// </summary>
public static class V2Store
{
    /// <summary>The newest book frame per instrument. One row each — the profile is read as a
    /// shape, and twenty-five levels of history would be a chart, not a shape.</summary>
    public static async Task<IReadOnlyDictionary<int, BookFrame>> BooksAsync(
        DbConnection conn, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, BookFrame>();
        }

        var rows = await conn.QueryAsync<BookFrame>(new CommandDefinition(
            """
            select distinct on (b.exchange_instrument_id)
                   b.exchange_instrument_id as "InstrumentId",
                   b.observed_at            as "ObservedAt",
                   b.levels                 as "Levels",
                   b.bid_px::double precision[]  as "BidPx",
                   b.bid_qty::double precision[] as "BidQty",
                   b.ask_px::double precision[]  as "AskPx",
                   b.ask_qty::double precision[] as "AskQty"
              from book_topn b
             where b.exchange_instrument_id = any(@ids)
               and b.observed_at > now() - interval '10 minutes'
             order by b.exchange_instrument_id, b.observed_at desc
            """,
            new { ids = ids.ToArray() }, cancellationToken: ct));

        return rows.ToDictionary(r => r.InstrumentId);
    }

    /// <summary>The last fills across every listing of the asset, newest first. The tape is the one
    /// thing on the page that is a stream rather than a reading, so it is read as one list across
    /// venues rather than per venue.</summary>
    public static async Task<IReadOnlyList<TapeRow>> TapeAsync(
        DbConnection conn, IReadOnlyList<int> ids, int limit, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await conn.QueryAsync<TapeRow>(new CommandDefinition(
            """
            select t.exchange_instrument_id as "InstrumentId",
                   t.event_time            as "EventTime",
                   t.price::double precision as "Price",
                   t.qty::double precision   as "Qty",
                   t.taker_side            as "TakerSide",
                   t.trade_type            as "TradeType"
              from trade t
             where t.exchange_instrument_id = any(@ids)
               and t.event_time > now() - interval '1 hour'
             order by t.event_time desc
             limit @limit
            """,
            new { ids = ids.ToArray(), limit }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// How much of the last day each dataset actually holds, per listing.
    ///
    /// Expressed as a share of the window rather than as a row count on purpose: a count says
    /// nothing without knowing what a full count would be, and the number of rows a full day
    /// holds differs by dataset and by venue. A share answers the question the band asks —
    /// "is this complete" — in one number that means the same thing in every cell.
    /// </summary>
    public static async Task<IReadOnlyList<CoverageCell>> CoverageAsync(
        DbConnection conn, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await conn.QueryAsync<CoverageCell>(new CommandDefinition(
            """
            select c.exchange_instrument_id as "InstrumentId",
                   c.dataset_code           as "Dataset",
                   least(100, round(100.0 * extract(epoch from sum(
                       least(c.range_to, now()) - greatest(c.range_from, now() - interval '1 day')))
                       / 86400.0))::int     as "PercentHeld",
                   max(c.requested_at)      as "LastAsked",
                   count(*) filter (where c.reason is not null)::int as "Short"
              from coverage c
             where c.exchange_instrument_id = any(@ids)
               and c.range_to > now() - interval '1 day'
             group by 1, 2
            """,
            new { ids = ids.ToArray() }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>Where a stream broke, still open first. A gap with no end is the page's loudest
    /// statement about itself, so it is not folded in with the closed ones.</summary>
    public static async Task<IReadOnlyList<GapRow>> GapsAsync(
        DbConnection conn, IReadOnlyList<int> ids, string[] segments, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<GapRow>(new CommandDefinition(
            """
            select g.segment_code as "SegmentCode",
                   g.collector    as "Collector",
                   g.gap_start    as "GapStart",
                   g.gap_end      as "GapEnd",
                   g.cause        as "Cause",
                   g.detail       as "Detail"
              from collector_gap g
             where (g.exchange_instrument_id = any(@ids) or g.exchange_instrument_id is null)
               and g.segment_code = any(@segments)
               and (g.gap_end is null or g.gap_end > now() - interval '1 day')
             order by (g.gap_end is null) desc, g.gap_start desc
             limit 12
            """,
            new { ids = ids.ToArray(), segments }, cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>Liquidation volume in the last buckets, per listing — the stress column.</summary>
    public static async Task<IReadOnlyDictionary<int, StressRow>> StressAsync(
        DbConnection conn, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, StressRow>();
        }

        var rows = await conn.QueryAsync<StressRow>(new CommandDefinition(
            """
            select l.exchange_instrument_id as "InstrumentId",
                   sum(l.volume)::double precision as "Volume",
                   max(l.volume_unit)       as "Unit",
                   max(l.bucket_time)       as "LatestBucket"
              from liquidation_volume_history l
             where l.exchange_instrument_id = any(@ids)
               and l.bucket_time > now() - interval '1 hour'
             group by 1
            """,
            new { ids = ids.ToArray() }, cancellationToken: ct));

        return rows.ToDictionary(r => r.InstrumentId);
    }
}

public sealed record BookFrame(
    int InstrumentId, DateTime ObservedAt, short Levels,
    double[] BidPx, double[] BidQty, double[] AskPx, double[] AskQty);

public sealed record TapeRow(
    int InstrumentId, DateTime EventTime, double Price, double Qty, string TakerSide, string? TradeType);

public sealed record CoverageCell(int InstrumentId, string Dataset, int PercentHeld, DateTime LastAsked, int Short);

public sealed record GapRow(
    string SegmentCode, string Collector, DateTime GapStart, DateTime? GapEnd, string Cause, string? Detail);

public sealed record StressRow(int InstrumentId, double Volume, string Unit, DateTime LatestBucket);

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
    /// <summary>
    /// What a market with no book has INSTEAD of one: the inputs its price impact is computed from,
    /// its open interest by side, and the ceiling it is held under.
    ///
    /// Newest reading per instrument, and only a recent one — these are observations, and a stale
    /// one presented beside a live book would be the same lie the whole page is built to avoid. Ten
    /// minutes matches BooksAsync's own window for the same reason.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, VaultPairRow>> VaultPairsAsync(
        DbConnection conn, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, VaultPairRow>();
        }

        var rows = await conn.QueryAsync<VaultPairRow>(new CommandDefinition(
            """
            select distinct on (v.exchange_instrument_id)
                   v.exchange_instrument_id as "InstrumentId",
                   v.received_at            as "At",
                   v.oi_long_quote          as "OiLongQuote",
                   v.oi_short_quote         as "OiShortQuote",
                   v.oi_max_quote           as "OiMaxQuote",
                   v.depth_above_1pct       as "DepthAbove1Pct",
                   v.depth_below_1pct       as "DepthBelow1Pct",
                   v.liquidity_buy          as "LiquidityBuy",
                   v.liquidity_sell         as "LiquiditySell",
                   v.spread_p               as "SpreadPercent"
              from vault_pair_state v
             where v.exchange_instrument_id = any(@ids)
               and v.received_at > now() - interval '10 minutes'
             order by v.exchange_instrument_id, v.received_at desc
            """,
            new { ids = ids.ToArray() }, cancellationToken: ct));

        return rows.ToDictionary(r => r.InstrumentId);
    }

    public static async Task<IReadOnlyDictionary<int, BookFrame>> BooksAsync(
        DbConnection conn, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, BookFrame>();
        }

        // Read by hand rather than through Dapper. Npgsql reports a `double precision[]` column
        // as System.Array — it will not commit to a rank before it has read a value — so Dapper
        // hunts for a BookFrame(int, DateTime, short, Array, Array, Array, Array) constructor,
        // finds none, and throws at materialization. GetFieldValue<double[]> asks for the rank.
        var books = new Dictionary<int, BookFrame>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select distinct on (b.exchange_instrument_id)
                   b.exchange_instrument_id,
                   b.observed_at,
                   b.levels,
                   b.bid_px::double precision[],
                   b.bid_qty::double precision[],
                   b.ask_px::double precision[],
                   b.ask_qty::double precision[]
              from book_topn b
             where b.exchange_instrument_id = any(@ids)
               and b.observed_at > now() - interval '10 minutes'
             order by b.exchange_instrument_id, b.observed_at desc
            """;

        var p = cmd.CreateParameter();
        p.ParameterName = "ids";
        p.Value = ids.ToArray();
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var frame = new BookFrame(
                reader.GetInt32(0),
                reader.GetDateTime(1),
                reader.GetInt16(2),
                await reader.GetFieldValueAsync<double[]>(3, ct),
                await reader.GetFieldValueAsync<double[]>(4, ct),
                await reader.GetFieldValueAsync<double[]>(5, ct),
                await reader.GetFieldValueAsync<double[]>(6, ct));

            books[frame.InstrumentId] = frame;
        }

        return books;
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

        // A CEILING PER LISTING, not the newest N outright.
        //
        // Outright, one busy venue owns the tape: on ADA all eighteen rows were WEEX, and the other
        // four listings were not absent from the market — they were absent from the page. That is
        // the band failing at exactly the thing it exists for, because a tape of one venue is a
        // tape you could have read on that venue.
        //
        // The ceiling is a share of the limit rather than a fixed number, so an asset listed twice
        // and an asset listed seven times both fill the same eighteen rows. Below the ceiling
        // nothing is held back: a venue with two fills in the hour shows two, and the quiet venue
        // reads as quiet rather than as trimmed.
        var perListing = Math.Max(3, (int)Math.Ceiling((double)limit / ids.Count));

        var rows = await conn.QueryAsync<TapeRow>(new CommandDefinition(
            """
            select x."InstrumentId", x."EventTime", x."Price", x."Qty", x."TakerSide", x."TradeType"
              from (
                    select t.exchange_instrument_id   as "InstrumentId",
                           t.event_time               as "EventTime",
                           t.price::double precision  as "Price",
                           t.qty::double precision    as "Qty",
                           t.taker_side               as "TakerSide",
                           t.trade_type               as "TradeType",
                           row_number() over (partition by t.exchange_instrument_id
                                              order by t.event_time desc) as rn
                      from trade t
                     where t.exchange_instrument_id = any(@ids)
                       and t.event_time > now() - interval '1 hour'
                   ) x
             where x.rn <= @perListing
             order by x."EventTime" desc
             limit @limit
            """,
            new { ids = ids.ToArray(), limit, perListing }, cancellationToken: ct));

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

    /// <summary>How many individual <c>coverage</c> rows a disclosed dataset cell shows before the
    /// panel stops rather than scrolling. A day at a busy dataset's own request cadence stays well
    /// under this; it exists as a backstop, not a tuned figure.</summary>
    public const int MaxCoverageRows = 100;

    /// <summary>
    /// B4: band 5's coverage ROW disclosed — every request the last 24 h actually made for this
    /// dataset, across every venue on the page, newest first. <c>range_from</c> is named for what it
    /// is in the model (<see cref="CoverageDetailRow"/>'s own doc) rather than trusted to the column
    /// name alone: it is the oldest row the venue actually RETURNED, not the floor of what was asked
    /// for — the two differ exactly when the venue answered <c>limit_hit</c> or cut the call short.
    /// </summary>
    public static async Task<IReadOnlyList<CoverageDetailRow>> CoverageDetailAsync(
        DbConnection conn, IReadOnlyList<int> instrumentIds, string dataset, CancellationToken ct)
    {
        if (instrumentIds.Count == 0)
        {
            return [];
        }

        var rows = await conn.QueryAsync<CoverageDetailRow>(new CommandDefinition(
            """
            select c.exchange_instrument_id     as "InstrumentId",
                   x.name || ' ' || i.exchange_symbol as "Venue",
                   c.range_from                 as "RangeFrom",
                   c.range_to                   as "RangeTo",
                   c.returned                   as "Returned",
                   c.requested_at               as "RequestedAt",
                   c.run_id                     as "RunId"
              from coverage c
              join exchange_instrument i on i.id = c.exchange_instrument_id
              join segment sg on sg.code = i.segment_code
              join exchange x on x.code = sg.exchange_code
             where c.exchange_instrument_id = any(@instrumentIds)
               and c.dataset_code = @dataset
               and c.range_to > now() - interval '1 day'
             order by c.exchange_instrument_id, c.requested_at desc
             limit @limit
            """,
            new { instrumentIds = instrumentIds.ToArray(), dataset, limit = MaxCoverageRows },
            cancellationToken: ct));

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

    /// <summary>
    /// Rolling 24h liquidation volume, per listing — the ONE series band 1's LIQUIDATIONS column
    /// and band 2's liquidations cut both read (P0-5, UX audit).
    ///
    /// Was a rolling 1 HOUR sum, read only by band 1; band 2's own liquidations cut read a
    /// different series entirely (the last CLOSED hourly bucket from VenueRowModel.Liquidations) —
    /// two different windows on two different tables, so the same venue could print two different
    /// numbers a scroll apart with nothing on the page to say they were answering different
    /// questions. 24 h to match TURNOVER 24H's own window, which sits one column over.
    /// </summary>
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
               and l.bucket_time > now() - interval '1 day'
             group by 1
            """,
            new { ids = ids.ToArray() }, cancellationToken: ct));

        return rows.ToDictionary(r => r.InstrumentId);
    }

    /// <summary>
    /// The busiest second this asset has had in the last hour, across every listing.
    ///
    /// Band 4 leads with it, and it is COUNTED rather than carried over from the mock: "up to
    /// 3 400 fills a second" is a claim about this market, so it has to come from this market. It
    /// cannot be taken off the eighteen rows the tape shows — those are a sample of a sample, and
    /// the busiest second is exactly what a sample of eighteen will miss.
    /// </summary>
    public static async Task<int> TapePeakAsync(
        DbConnection conn, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            select coalesce(max(n), 0)::int
              from (
                    select count(*) as n
                      from trade t
                     where t.exchange_instrument_id = any(@ids)
                       and t.event_time > now() - interval '1 hour'
                     group by date_trunc('second', t.event_time)
                   ) x
            """,
            new { ids = ids.ToArray() }, cancellationToken: ct)) ?? 0;
    }

    /// <summary>
    /// What each segment is SET to collect, per dataset.
    ///
    /// Band 5's grid used to list only the datasets that had written coverage rows, which made the
    /// band silent exactly where it should speak: a dataset nobody collected simply vanished from
    /// it, and a reader could not tell "we hold none of this" from "there is no such thing here".
    ///
    /// The matrix is full by construction — 0014 and 0034 both insist on a row for every
    /// (segment, dataset) pair, even where the venue cannot — so an ABSENT row means the pair was
    /// never decided, and 'disabled' means it was decided against. Those are different sentences and
    /// the grid prints them differently.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(string Segment, string Dataset), string>> DatasetModesAsync(
        DbConnection conn, IReadOnlyList<string> segments, CancellationToken ct)
    {
        if (segments.Count == 0)
        {
            return new Dictionary<(string, string), string>();
        }

        var rows = await conn.QueryAsync<DatasetMode>(new CommandDefinition(
            """
            select sd.segment_code as "SegmentCode",
                   sd.dataset_code as "DatasetCode",
                   sd.mode         as "Mode"
              from segment_dataset sd
             where sd.segment_code = any(@segments)
            """,
            new { segments = segments.ToArray() }, cancellationToken: ct));

        return rows.ToDictionary(r => (r.SegmentCode, r.DatasetCode), r => r.Mode);
    }

    /// <summary>
    /// Hourly liquidation volume per instrument, on the SAME twenty-five windows every other series
    /// on the page is drawn on.
    ///
    /// The venues publish this as a ready aggregate rather than as our own count of forced fills,
    /// and every row we hold carries interval_s = 3600 and unit 'base' — so an hour is the bucket
    /// the venue itself chose, not a rollup we invented, and the sum below only adds buckets that
    /// fall in the same hour when a venue writes finer ones later.
    ///
    /// A window with no row is null and not zero: "no liquidation was reported for this hour" and
    /// "nothing was liquidated this hour" are the same sentence only when the collector ran, and
    /// this table cannot tell you whether it did — band 5 can.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<double?>>> LiquidationsAsync(
        DbConnection conn, IReadOnlyList<int> ids, DateTimeOffset at, CancellationToken ct)
    {
        var windows = CandleStore.Windows(at);
        if (ids.Count == 0 || windows.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<double?>>();
        }

        var rows = await conn.QueryAsync<LiquidationHour>(new CommandDefinition(
            """
            select l.exchange_instrument_id            as "InstrumentId",
                   date_trunc('hour', l.bucket_time)   as "Hour",
                   sum(l.volume)::double precision     as "Volume"
              from liquidation_volume_history l
             where l.exchange_instrument_id = any(@ids)
               and l.bucket_time >= @from
               and l.bucket_time < @to
             group by 1, 2
            """,
            new
            {
                ids = ids.ToArray(),
                from = windows[0],
                to = windows[^1].AddHours(1),
            },
            cancellationToken: ct));

        var byHour = rows.ToDictionary(r => (r.InstrumentId, r.Hour), r => r.Volume);

        return rows
            .Select(r => r.InstrumentId)
            .Distinct()
            .ToDictionary(
                id => id,
                id => (IReadOnlyList<double?>)windows
                    .Select(w => byHour.TryGetValue((id, w), out var v) ? v : (double?)null)
                    .ToList());
    }
}

/// <summary>
/// One vault-backed pair's state, as band 2 reads it. Every figure is the venue's own published
/// input — nothing here is computed by us, and none of it belongs in depth_*bps, which are
/// documented as a measurement of a book this market does not have.
/// </summary>
/// <param name="DepthAbove1Pct">The notional that moves the price one percent up, as published.
/// An input to the impact function, and the nearest thing this market has to a book's thickness —
/// but arrived at by a formula rather than by counting resting orders, which is why it is shown
/// under its own name and never in a depth column.</param>
public sealed record VaultPairRow(
    int InstrumentId,
    DateTime At,
    double? OiLongQuote,
    double? OiShortQuote,
    double? OiMaxQuote,
    double? DepthAbove1Pct,
    double? DepthBelow1Pct,
    double? LiquidityBuy,
    double? LiquiditySell,

    /// <summary>The venue's own fixed spread, in percent. NOT the same measurement as
    /// <c>PairVenueRow.SpreadBps</c>, which is (ask − bid) / mid off a book: this is a stated
    /// charge applied to the oracle price before size-dependent impact. Shown here, beside the
    /// other inputs, and deliberately never in the spread column — putting it there would be the
    /// same category error as putting these depth figures into depth_*bps.</summary>
    double? SpreadPercent);

public sealed record BookFrame(
    int InstrumentId, DateTime ObservedAt, short Levels,
    double[] BidPx, double[] BidQty, double[] AskPx, double[] AskQty);

public sealed record TapeRow(
    int InstrumentId, DateTime EventTime, double Price, double Qty, string TakerSide, string? TradeType);

public sealed record CoverageCell(int InstrumentId, string Dataset, int PercentHeld, DateTime LastAsked, int Short);

/// <summary>B4: one request behind a coverage cell.</summary>
/// <param name="RangeFrom">The OLDEST row the venue actually returned — not the floor of the
/// requested range. The two differ exactly when the venue cut the call short (limit_hit) or broke
/// mid-way (timeout/error); reading this as "what we asked for" understates how much the venue
/// really withheld.</param>
/// <param name="RunId">The collector_run this request belongs to, or null — the FK is
/// <c>on delete set null</c>, so a request whose run has since aged out of retention still has its
/// own row here with nothing to link back to.</param>
public sealed record CoverageDetailRow(
    int InstrumentId, string Venue,
    DateTime RangeFrom, DateTime RangeTo, int Returned, DateTime RequestedAt, long? RunId);

public sealed record GapRow(
    string SegmentCode, string Collector, DateTime GapStart, DateTime? GapEnd, string Cause, string? Detail);

public sealed record StressRow(int InstrumentId, double Volume, string Unit, DateTime LatestBucket);

public sealed record LiquidationHour(int InstrumentId, DateTime Hour, double Volume);

public sealed record DatasetMode(string SegmentCode, string DatasetCode, string Mode);

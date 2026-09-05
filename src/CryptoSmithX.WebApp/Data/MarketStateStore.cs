using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// The market as it was at one instant — one row per instrument, each carrying its own measurement
/// times rather than borrowing the page's.
///
/// This replaces the question the run pages tried and failed to answer. "What did run 646 produce"
/// is unanswerable without provenance on the rows, and joining by time window returned nothing for
/// roughly three passes in four. "What did the market look like at 12:04:31" is answerable from the
/// same data with no provenance at all, and it is the question an operator actually has.
///
/// It is also the shape <c>as_of(instrument, t)</c> will take when the point-in-time layer is built,
/// so the query is written here the way that function will need it: newest observation at or before
/// T, never the next one, and an instrument with nothing before T comes back as a row of nulls
/// rather than vanishing from the result.
/// </summary>
public static class MarketStateStore
{
    /// <summary>
    /// Measured on the live test database at 8.16M snapshot rows: 47–99 ms for all 1,561 collected
    /// instruments, one index descent per instrument on <c>market_snapshot_pkey</c> scanned backwards
    /// and stopped at the first row. A lower time bound was tried and rejected — it bought nothing
    /// measurable and it turns "stale" into "absent", which is exactly the lie this page exists to
    /// avoid. DISTINCT ON over a window lost badly: it has to read every row in the range.
    /// </summary>
    private const string Sql =
        """
        select i.id                                                   as "InstrumentId",
               i.segment_code                                        as "SegmentCode",
               i.exchange_symbol                                      as "Symbol",
               i.base_asset                                           as "BaseAsset",
               i.quote_asset                                          as "QuoteAsset",
               i.status                                               as "Status",
               i.first_seen_at                                        as "FirstSeenAt",

               s.received_at                                          as "ReceivedAt",
               extract(epoch from (@at - s.received_at))::double precision as "PriceLagSeconds",
               s.last_price                                           as "LastPrice",
               s.bid_price                                            as "BidPrice",
               s.ask_price                                            as "AskPrice",
               -- Spread is computed, never stored: 0001 is explicit that a derived number does not
               -- get a column, because then two places could disagree about it.
               (s.ask_price - s.bid_price)
                 / nullif((s.ask_price + s.bid_price) / 2, 0) * 10000  as "SpreadBps",
               s.bid_size                                             as "BidSize",
               s.ask_size                                             as "AskSize",
               s.mark_price                                           as "MarkPrice",
               s.funding_rate                                         as "FundingRate",
               s.turnover_24h                                         as "Turnover24h",

               -- Open interest arrives on its own call on some venues, so it keeps its own clock.
               s.open_interest                                        as "OpenInterest",
               extract(epoch from (@at - s.open_interest_at))::double precision as "OpenInterestLagSeconds",

               -- And depth on a third, much slower one. A WEEX sweep is ~470 s wide, so the tail of
               -- that venue carries a book minutes older than its head — the single most misleading
               -- thing about a naive "snapshot at T", and the reason this lag is its own column.
               s.depth_at                                             as "DepthAt",
               extract(epoch from (@at - s.depth_at))::double precision as "DepthLagSeconds",
               s.depth_bid_25bps                                      as "DepthBid25",
               s.depth_ask_25bps                                      as "DepthAsk25"

          from exchange_instrument i
          left join lateral (
              select s.*
                from market_snapshot s
               where s.exchange_instrument_id = i.id
                 and s.received_at <= @at
               order by s.received_at desc
               limit 1
          ) s on true
         where i.collect
           and (@segment is null or i.segment_code = @segment)
         order by i.segment_code, i.exchange_symbol
        """;

    public static async Task<MarketStateSlice> AtAsync(
        DbConnection conn, DateTime at, string? segment, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<MarketStateRow>(new CommandDefinition(
            Sql, new { at, segment }, cancellationToken: ct))).ToList();

        // Gaps covering T are the difference between "the venue was quiet" and "we were not looking",
        // and without them an absent row is unreadable. The standing limit is stated on the page
        // rather than hidden: gaps are recorded per segment and per collector only, and only when a
        // whole pass failed, so an absence with no gap is a cause we do not know — not a quiet market.
        var gaps = (await conn.QueryAsync<CollectorGapRow>(new CommandDefinition(
            """
            select g.collector                                            as "Collector",
                   g.gap_start                                            as "GapStart",
                   g.gap_end                                              as "GapEnd",
                   g.cause                                                as "Cause",
                   g.segment_code                                        as "Detail",
                   extract(epoch from coalesce(g.gap_end, now()) - g.gap_start)::double precision
                                                                          as "SecondsLong"
              from collector_gap g
             where (@segment is null or g.segment_code = @segment)
               and g.gap_start <= @at
               and coalesce(g.gap_end, now()) >= @at
             order by g.gap_start
            """,
            new { at, segment }, cancellationToken: ct))).ToList();

        var earliest = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "select min(received_at) from market_snapshot", cancellationToken: ct));

        // How often an observation is KEPT, per segment. It was one number for the whole page until
        // 0020 made it a per-cell cascade; a single figure would now be a plain lie the moment two
        // venues differ, and this page exists to say what is true about a moment. Floored at the
        // poll interval, exactly as SettingsSnapshot.HistoryInterval does in the hub.
        var keptEverySeconds = (await conn.QueryAsync<(string Segment, int Seconds)>(new CommandDefinition(
            """
            select sd.segment_code as "Segment",
                   greatest(coalesce(sd.history_interval_s, d.default_history_interval_s,
                                     sd.interval_s, d.default_interval_s),
                            coalesce(sd.interval_s, d.default_interval_s))::int as "Seconds"
              from segment_dataset sd
              join dataset d on d.code = sd.dataset_code
             where sd.dataset_code = 'snapshot'
               and (@segment is null or sd.segment_code = @segment)
               and coalesce(sd.interval_s, d.default_interval_s) is not null
            """,
            new { segment }, cancellationToken: ct)))
            .ToDictionary(r => r.Segment, r => r.Seconds, StringComparer.Ordinal);

        var segments = (await conn.QueryAsync<string>(new CommandDefinition(
            "select code from segment where status = 'enabled' order by code", cancellationToken: ct))).ToList();

        return new MarketStateSlice(at, segment, rows, gaps, earliest, keptEverySeconds, segments);
    }
}

using System.Data.Common;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// One trading pair across every platform that lists it. Two reads, kept apart on purpose.
///
/// Top of book uses the newest-at-or-before-T lateral, the same shape MarketStateStore documents at
/// 47–99 ms for 1,561 instruments: one index descent per instrument on the primary key, scanned
/// backwards and stopped at the first row. Each platform's row carries its own lag, because the
/// platforms are not observed together and pretending otherwise is the one thing this console must
/// not do.
///
/// Candles are read over a shared window list. Every platform's window covers the same wall clock,
/// so the charts can be stacked and read vertically — which is the only cross-platform comparison
/// this data supports without a timing assumption.
/// </summary>
public static class PairStore
{
    /// <summary>Pairs that at least one platform lists, newest listing first for the picker.</summary>
    public static async Task<IReadOnlyList<(string Base, string Quote, int Venues)>> ListAsync(
        DbConnection conn, string? search, CancellationToken ct) =>
        (await conn.QueryAsync<(string Base, string Quote, int Venues)>(new CommandDefinition(
            """
            select i.base_asset  as "Base",
                   i.quote_asset as "Quote",
                   count(distinct i.segment_code)::int as "Venues"
              from exchange_instrument i
             where i.collect
               and (@search = '' or i.base_asset ilike @like or i.quote_asset ilike @like)
             group by 1, 2
             order by count(distinct i.segment_code) desc, 1, 2
            """,
            new { search = (search ?? "").Trim(), like = $"%{(search ?? "").Trim()}%" },
            cancellationToken: ct))).ToList();

    public static async Task<PairAtInstant?> AtAsync(
        DbConnection conn, string baseAsset, string quote, DateTime at, short timeframe,
        int windows, CancellationToken ct)
    {
        var venues = (await conn.QueryAsync<PairVenueRow>(new CommandDefinition(
            """
            select i.id                   as "InstrumentId",
                   i.segment_code         as "SegmentCode",
                   sg.exchange_code       as "ExchangeCode",
                   x.name                 as "ExchangeName",
                   i.exchange_symbol      as "Symbol",
                   i.contract_multiplier  as "ContractMultiplier",
                   i.status               as "Status",
                   i.collect              as "Collect",
                   s.received_at          as "ReceivedAt",
                   extract(epoch from (@at - s.received_at))::double precision      as "PriceLagSeconds",
                   s.bid_price            as "BidPrice",
                   s.ask_price            as "AskPrice",
                   s.last_price           as "LastPrice",
                   s.mark_price           as "MarkPrice",
                   s.index_price          as "IndexPrice",
                   s.bid_size             as "BidSize",
                   s.ask_size             as "AskSize",
                   s.turnover_24h         as "Turnover24h",
                   s.funding_rate         as "FundingRate",
                   s.open_interest        as "OpenInterest",
                   extract(epoch from (@at - s.open_interest_at))::double precision as "OpenInterestLagSeconds",
                   s.depth_bid_10bps      as "DepthBid10",
                   s.depth_ask_10bps      as "DepthAsk10",
                   s.depth_bid_25bps      as "DepthBid25",
                   s.depth_ask_25bps      as "DepthAsk25",
                   s.depth_bid_50bps      as "DepthBid50",
                   s.depth_ask_50bps      as "DepthAsk50",
                   s.depth_at             as "DepthAt",
                   extract(epoch from (@at - s.depth_at))::double precision        as "DepthLagSeconds"
              from exchange_instrument i
              join segment  sg on sg.code = i.segment_code
              join exchange x  on x.code  = sg.exchange_code
              left join lateral (
                  select s.*
                    from market_snapshot s
                   where s.exchange_instrument_id = i.id and s.received_at <= @at
                   order by s.received_at desc
                   limit 1
              ) s on true
             where i.base_asset = @baseAsset and i.quote_asset = @quote
             order by i.segment_code
            """,
            new { baseAsset, quote, at }, cancellationToken: ct))).ToList();

        if (venues.Count == 0)
        {
            return null;
        }

        // The window list is computed, not read: every platform must be drawn against the SAME
        // windows, including the ones where it has no bar. Reading the union of what exists would
        // silently close the gaps — a platform that went dark for ten minutes would have its
        // remaining candles slide together and look continuous.
        var anchor = PairAtInstant.Anchor(at, timeframe);
        var windowList = Enumerable.Range(0, windows)
            .Select(i => anchor.AddMinutes(-timeframe * (windows - 1 - i)))
            .ToList();
        var from = windowList[0];

        var rows = (await conn.QueryAsync<(string SegmentCode, DateTime OpenTime, double Open, double High, double Low, double Close, double Volume, short BarCount)>(
            new CommandDefinition(
                """
                select i.segment_code as "SegmentCode",
                       c.open_time    as "OpenTime",
                       c.open         as "Open",
                       c.high         as "High",
                       c.low          as "Low",
                       c.close        as "Close",
                       c.volume       as "Volume",
                       c.bar_count    as "BarCount"
                  from exchange_instrument i
                  join market_candle c
                    on c.exchange_instrument_id = i.id
                   and c.timeframe = @timeframe
                   and c.open_time between @from and @anchor
                 where i.base_asset = @baseAsset and i.quote_asset = @quote
                """,
                new { baseAsset, quote, timeframe = (int)timeframe, from, anchor },
                cancellationToken: ct))).ToList();

        var bySegment = rows
            .GroupBy(r => r.SegmentCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.OpenTime), StringComparer.Ordinal);

        var series = venues
            .Select(v =>
            {
                bySegment.TryGetValue(v.SegmentCode, out var byTime);
                var candles = windowList
                    .Select(w => byTime is not null && byTime.TryGetValue(w, out var r)
                        ? new PairCandle(r.OpenTime, r.Open, r.High, r.Low, r.Close, r.Volume, r.BarCount, timeframe)
                        : null)
                    .ToList();
                return new PairVenueSeries(v.SegmentCode, v.Symbol, v.ContractMultiplier, candles);
            })
            .ToList();

        return new PairAtInstant(baseAsset, quote, at, timeframe, windowList, venues, series);
    }
}

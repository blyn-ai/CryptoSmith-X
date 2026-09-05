using System.Data.Common;
using System.Text;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// Every instrument on every exchange, and the read behind one instrument's detail page. All figures
/// are derived from the latest snapshot, the candle and metric tables — the instrument row itself
/// carries only identity, the observed status and the operator's collect decision. The one write is
/// the collect toggle, which stamps who flipped it and why.
/// </summary>
public static class InstrumentStore
{
    /// <summary>The timeframes the detail chart offers, in minutes (1m/5m/15m/1h/4h).</summary>
    public static readonly IReadOnlyList<int> Timeframes = [1, 5, 15, 60, 240];

    public static async Task<InstrumentPage> ListAsync(
        DbConnection conn, string? segment, string? status, bool onlyTrading, string? search,
        string sort, int page, int pageSize, CancellationToken ct)
    {
        var where = new StringBuilder("where 1 = 1");
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(segment))
        {
            where.Append(" and i.segment_code = @segment");
            p.Add("segment", segment);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Append(" and i.status = @status");
            p.Add("status", status);
        }

        if (onlyTrading)
        {
            where.Append(" and i.status = 'trading'");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Append(" and i.exchange_symbol ilike @like");
            p.Add("like", "%" + search.Trim() + "%");
        }

        // Whitelisted sort — never interpolate a user string into ORDER BY.
        var orderBy = sort switch
        {
            "oi" => "(l.open_interest * l.mark_price) desc nulls last, i.exchange_symbol",
            "funding" => "l.funding_rate desc nulls last, i.exchange_symbol",
            "age" => "l.received_at asc nulls first, i.exchange_symbol",
            _ => "i.segment_code, i.exchange_symbol",
        };

        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from exchange_instrument i {where}", p, cancellationToken: ct));

        p.Add("take", pageSize);
        p.Add("skip", (page - 1) * pageSize);

        var items = (await conn.QueryAsync<InstrumentListItem>(new CommandDefinition(
            $"""
             select i.id              as "Id",
                    i.segment_code   as "SegmentCode",
                    i.exchange_symbol as "Symbol",
                    i.base_asset      as "BaseAsset",
                    i.quote_asset     as "QuoteAsset",
                    i.status          as "Status",
                    i.collect         as "Collect",
                    l.last_price      as "LastPrice",
                    l.funding_rate    as "FundingRate",
                    l.open_interest * l.mark_price as "OpenInterestNotional",
                    extract(epoch from now() - l.received_at)::double precision as "SnapshotAgeSeconds"
               from exchange_instrument i
               left join market_snapshot_latest l on l.exchange_instrument_id = i.id
               {where}
              order by {orderBy}
              limit @take offset @skip
             """,
            p, cancellationToken: ct))).ToList();

        var segments = (await conn.QueryAsync<string>(new CommandDefinition(
            "select code from segment order by code", cancellationToken: ct))).ToList();

        return new InstrumentPage(items, total, page, pageSize, segments, segment, status, onlyTrading, search, sort);
    }

    public static async Task<InstrumentDetails?> GetAsync(DbConnection conn, int id, int timeframe, CancellationToken ct)
    {
        var tf = Timeframes.Contains(timeframe) ? timeframe : 1;

        var head = await conn.QuerySingleOrDefaultAsync<InstrumentHead>(new CommandDefinition(
            """
            select id                     as "Id",
                   segment_code          as "SegmentCode",
                   exchange_symbol        as "Symbol",
                   base_asset             as "BaseAsset",
                   quote_asset            as "QuoteAsset",
                   status                 as "Status",
                   status_changed_at      as "StatusChangedAt",
                   listed_at              as "ListedAt",
                   first_seen_at          as "FirstSeenAt",
                   last_seen_at           as "LastSeenAt",
                   collect                as "Collect",
                   collect_note           as "CollectNote",
                   collect_changed_at     as "CollectChangedAt",
                   collect_changed_by     as "CollectChangedBy",
                   funding_interval_hours as "FundingIntervalHours"
              from exchange_instrument
             where id = @id
            """,
            new { id },
            cancellationToken: ct));
        if (head is null)
        {
            return null;
        }

        var snapshot = await conn.QuerySingleOrDefaultAsync<SnapshotView>(new CommandDefinition(
            """
            select received_at    as "ReceivedAt",
                   extract(epoch from now() - received_at)::double precision as "SnapshotAgeSeconds",
                   last_price     as "LastPrice",
                   bid_price      as "BidPrice",
                   ask_price      as "AskPrice",
                   bid_size       as "BidSize",
                   ask_size       as "AskSize",
                   case when (bid_price + ask_price) > 0
                        then (ask_price - bid_price) / ((bid_price + ask_price) / 2) * 10000
                   end            as "SpreadBps",
                   mark_price     as "MarkPrice",
                   index_price    as "IndexPrice",
                   funding_rate   as "FundingRate",
                   turnover_24h   as "Turnover24h",
                   open_interest  as "OpenInterest",
                   open_interest * mark_price as "OpenInterestNotional",
                   depth_bid_10bps as "DepthBid10",
                   depth_ask_10bps as "DepthAsk10",
                   depth_bid_25bps as "DepthBid25",
                   depth_ask_25bps as "DepthAsk25",
                   depth_bid_50bps as "DepthBid50",
                   depth_ask_50bps as "DepthAsk50",
                   depth_at        as "DepthAt",
                   extract(epoch from now() - depth_at)::double precision as "DepthAgeSeconds"
              from market_snapshot_latest
             where exchange_instrument_id = @id
            """,
            new { id },
            cancellationToken: ct));

        var candles = (await conn.QueryAsync<CandlePoint>(new CommandDefinition(
            """
            select open_time as "OpenTime", open as "Open", high as "High", low as "Low", close as "Close"
              from (select open_time, open, high, low, close
                      from market_candle
                     where exchange_instrument_id = @id and timeframe = @tf
                     order by open_time desc
                     limit 120) recent
             order by open_time
            """,
            new { id, tf },
            cancellationToken: ct))).ToList();

        var metrics = (await conn.QueryAsync<MetricPoint>(new CommandDefinition(
            """
            select hour_time          as "HourTime",
                   open_interest_last as "OpenInterestLast",
                   funding_rate_last  as "FundingRateLast",
                   spread_bps_avg     as "SpreadBpsAvg"
              from market_metric_hour
             where exchange_instrument_id = @id and hour_time >= now() - interval '48 hours'
             order by hour_time
            """,
            new { id },
            cancellationToken: ct))).ToList();

        var funding = (await conn.QueryAsync<FundingRow>(new CommandDefinition(
            """
            select funding_time as "FundingTime", rate as "Rate"
              from funding_rate_history
             where exchange_instrument_id = @id
             order by funding_time desc
             limit 20
            """,
            new { id },
            cancellationToken: ct))).ToList();

        var coverage = await LoadCoverageAsync(conn, id, head.SegmentCode, head.Status, head.Collect, snapshot?.SnapshotAgeSeconds, ct);

        // The same canonical asset on other venues — the one-click hop between exchanges.
        var siblings = (await conn.QueryAsync<SiblingListing>(new CommandDefinition(
            """
            select i.id as "Id", i.segment_code as "SegmentCode", i.exchange_symbol as "Symbol"
              from exchange_instrument i
             where i.base_asset = @baseAsset and i.quote_asset = @quoteAsset and i.id <> @id
             order by i.segment_code
            """,
            new { baseAsset = head.BaseAsset, quoteAsset = head.QuoteAsset, id },
            cancellationToken: ct))).ToList();

        return new InstrumentDetails(
            head.Id, head.SegmentCode, head.Symbol, head.BaseAsset, head.QuoteAsset, head.Status,
            head.StatusChangedAt, head.ListedAt, head.FirstSeenAt, head.LastSeenAt,
            head.Collect, head.CollectNote, head.CollectChangedAt, head.CollectChangedBy, head.FundingIntervalHours,
            snapshot, tf, Timeframes, candles, metrics, funding, coverage, siblings);
    }

    /// <summary>Writes the collect toggle and its note, stamping who and when. Returns false if the
    /// instrument does not exist.</summary>
    public static async Task<bool> SaveCollectAsync(
        DbConnection conn, int id, bool collect, string? note, string? changedBy, CancellationToken ct)
    {
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            update exchange_instrument
               set collect            = @collect,
                   collect_note       = nullif(@note, ''),
                   collect_changed_at = now(),
                   collect_changed_by = @changedBy
             where id = @id
            """,
            new { id, collect, note, changedBy },
            cancellationToken: ct));
        return rows == 1;
    }

    private static async Task<CoverageView> LoadCoverageAsync(
        DbConnection conn, int id, string segmentCode, string status, bool collect, double? snapshotAge, CancellationToken ct)
    {
        var c = await conn.QuerySingleAsync<(int Minutes24h, DateTime? CandleFrom, DateTime? CandleTo, double? LastCandleAge)>(
            new CommandDefinition(
                """
                select count(*) filter (where open_time >= now() - interval '24 hours')::int as "Minutes24h",
                       min(open_time) as "CandleFrom",
                       max(open_time) as "CandleTo",
                       extract(epoch from now() - max(open_time))::double precision as "LastCandleAge"
                  from market_candle
                 where exchange_instrument_id = @id and timeframe = 1
                """,
                new { id },
                cancellationToken: ct));

        var f = await conn.QuerySingleAsync<(DateTime? FundingFrom, DateTime? FundingTo, double? LastFundingAge)>(
            new CommandDefinition(
                """
                select min(funding_time) as "FundingFrom",
                       max(funding_time) as "FundingTo",
                       extract(epoch from now() - max(funding_time))::double precision as "LastFundingAge"
                  from funding_rate_history
                 where exchange_instrument_id = @id
                """,
                new { id },
                cancellationToken: ct));

        // Effective snapshot interval: the segment_dataset override, or the dataset default.
        // Fixes a bug 0014 (datasets) left behind — snapshot_interval_s moved off `exchange` and
        // this query was not updated with it, so this page 500'd on every visit since that migration.
        var interval = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            select coalesce(ec.interval_s, c.default_interval_s)
              from dataset c
              left join segment_dataset ec on ec.segment_code = @segmentCode and ec.dataset_code = c.code
             where c.code = 'snapshot'
            """,
            new { segmentCode },
            cancellationToken: ct));

        // The exchange is collecting if it is enabled and its snapshot loop has succeeded recently.
        var exchangeCollecting = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1 from segment e
                 where e.code = @segmentCode and e.status = 'enabled'
                   and exists (select 1 from collector_status s
                                where s.segment_code = e.code and s.collector = 'snapshot'
                                  and s.last_success_at > now() - interval '3 minutes'))
            """,
            new { segmentCode },
            cancellationToken: ct));

        var minutes = c.Minutes24h;
        // Holes against a full-day yardstick of 1440 one-minute bars; the candle range below shows
        // when the instrument is simply younger than a day rather than genuinely gappy.
        var holes = Math.Max(0, 1440 - minutes);
        // A live exchange but a silent instrument: it should be reporting and is not.
        var silent = collect && status == "trading" && exchangeCollecting
                     && (snapshotAge is null || snapshotAge > interval * 3.0);

        return new CoverageView(
            minutes, holes, c.CandleFrom, c.CandleTo, f.FundingFrom, f.FundingTo,
            c.LastCandleAge, f.LastFundingAge, exchangeCollecting, silent);
    }

    private sealed record InstrumentHead(
        int Id, string SegmentCode, string Symbol, string BaseAsset, string QuoteAsset, string Status,
        DateTime StatusChangedAt, DateTime? ListedAt, DateTime FirstSeenAt, DateTime LastSeenAt,
        bool Collect, string? CollectNote, DateTime? CollectChangedAt, string? CollectChangedBy,
        short FundingIntervalHours);
}

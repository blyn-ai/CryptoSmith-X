using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Writes the current state of every instrument. <c>market_snapshot_latest</c> is upserted on every
/// pass; the same rows are appended to the history every <c>history_interval_s</c> seconds, which
/// resolves per segment×dataset cell (0020).
/// A row carries one instant — its <c>received_at</c> — and every figure in it was observed at that
/// instant or not observed at all. What CHANGED (0030, and the collector rewrite that followed it):
/// a missing figure is now a NULL in its own column, where it used to cost the whole observation.
/// The rule it replaced was right while the columns were NOT NULL and wrong the moment they were
/// not: a spot market has no funding, a vault-backed perp has no bid, and under the old rule
/// neither could be recorded at all. What has NOT changed is the reason that rule existed — an
/// absence is never completed with a zero, and never with a NaN either (see <see cref="Figures"/>).
/// </summary>
public sealed class SnapshotCollector
{
    // Instruments to snapshot: everything the venue lists for us, minus the ones an operator turned
    // collect off for. A ticker for a skipped symbol then finds no id below and is ignored.
    internal const string TargetInstrumentsSql =
        "select exchange_symbol, id from exchange_instrument "
        + "where segment_code = @code and collect = true";

    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;
    private readonly DbSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private long _lastHistoryBucket = -1;
    private long _lastHistoryIntervalS = -1;

    public SnapshotCollector(
        IExchangeMarketData adapter, Db db, DbSettings settings, TimeProvider clock, ILogger logger)
    {
        _adapter = adapter;
        _db = db;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var tickers = await _adapter.GetTickersAsync(ct);
        if (tickers.Count == 0)
        {
            return 0;
        }

        await using var conn = await _db.OpenAsync(ct);

        var ids = (await conn.QueryAsync<(string Symbol, int Id)>(new CommandDefinition(
                TargetInstrumentsSql,
                new { code = _adapter.SegmentCode },
                cancellationToken: ct)))
            .ToDictionary(r => r.Symbol, r => r.Id, StringComparer.Ordinal);

        // How often an observation is kept, in seconds, read live from the database like every other
        // knob here. It used to be a constant minute, which meant five of every six observations
        // existed only in the mutable latest cache and were overwritten by the next pass — and
        // spread, top-of-book sizes, open interest and depth at a moment cannot be re-fetched from
        // any venue at any price, so those five sixths stopped existing anywhere. A site with disk
        // sets this to its poll interval and keeps everything it sees. Since 0020 it resolves per
        // cell, so a venue with 14 500 spot instruments can keep less often than one with 1 500
        // perps without either decision being made for the other.
        var historyInterval = (long)(await _settings.CurrentAsync(ct))
            .HistoryInterval(_adapter.SegmentCode, "snapshot").TotalSeconds;

        // Buckets are wall-clock aligned so restarts do not shift the phase. Changing the interval
        // rescales the bucket number, and two different intervals can land on the same number — so
        // a changed interval always writes, rather than silently skipping one keep at the boundary.
        var bucket = _clock.GetUtcNow().ToUnixTimeSeconds() / Math.Max(1, historyInterval);
        var writeHistory = bucket != _lastHistoryBucket || historyInterval != _lastHistoryIntervalS;
        if (writeHistory)
        {
            await Partitions.EnsureAsync(conn, _clock.GetUtcNow(), ct);
        }

        // Rows are gathered first and written in TWO statements, not two per instrument. The loop
        // that used to live here issued an upsert and an insert for every ticker: on Kraken's 275
        // instruments that is 550 round trips to postgres every pass, and it showed — the snapshot
        // pass measured 2.16 s while the venue side of it is a SINGLE bulk call. Once the venue
        // passes came down to fractions of a second (0038), our own writing was the slowest thing
        // left in the loop.
        //
        // Written with NpgsqlCommand rather than Dapper, and that is not a style choice: Dapper
        // treats any IEnumerable parameter as a list to expand into @p1, @p2, … which turns
        // unnest(@ids) into unnest((@ids1,@ids2,…)) and fails. Arrays go through the provider
        // directly, typed.
        var partial = 0;

        var ids_ = new List<int>(tickers.Count);
        var receivedAt = new List<DateTimeOffset>(tickers.Count);
        var last = new List<double?>(tickers.Count);
        var bid = new List<double?>(tickers.Count);
        var ask = new List<double?>(tickers.Count);
        var bidSize = new List<double?>(tickers.Count);
        var askSize = new List<double?>(tickers.Count);
        var mark = new List<double?>(tickers.Count);
        var index = new List<double?>(tickers.Count);
        var funding = new List<double?>(tickers.Count);
        var turnover = new List<double?>(tickers.Count);
        var oi = new List<double?>(tickers.Count);
        var oiAt = new List<DateTimeOffset?>(tickers.Count);
        var d10b = new List<double?>(tickers.Count);
        var d10a = new List<double?>(tickers.Count);
        var d25b = new List<double?>(tickers.Count);
        var d25a = new List<double?>(tickers.Count);
        var d50b = new List<double?>(tickers.Count);
        var d50a = new List<double?>(tickers.Count);
        var dAt = new List<DateTimeOffset?>(tickers.Count);
        var venueTs = new List<DateTimeOffset?>(tickers.Count);
        var lastTradeAt = new List<DateTimeOffset?>(tickers.Count);
        var fundingPredicted = new List<double?>(tickers.Count);
        var nextFundingAt = new List<DateTimeOffset?>(tickers.Count);
        var volume24hBase = new List<double?>(tickers.Count);
        var oiQuote = new List<double?>(tickers.Count);
        var marketOpen = new List<bool?>(tickers.Count);
        // Read from the ticker's own Depth, not recomputed here — DepthMath already produced them
        // (0039 phase 4 item 2). Depth null (not collected this frame) propagates through the
        // null-conditional as NULL on all three, distinct from a genuinely empty side (0 on
        // ReachBidBps/ReachAskBps when Depth exists but a side has no levels).
        var depthRef = new List<double?>(tickers.Count);
        var reachBid = new List<double?>(tickers.Count);
        var reachAsk = new List<double?>(tickers.Count);

        foreach (var t in tickers)
        {
            if (!ids.TryGetValue(t.ExchangeSymbol, out var id))
            {
                // Seen by the ticker call but not yet by discovery; it will exist next round.
                continue;
            }

            // The observation is WRITTEN, whatever it is missing. It used to be dropped whole if any
            // one of eight figures was absent — right while the columns were NOT NULL, and wrong
            // ever since 0030 lifted that: a spot market has no funding and no open interest by
            // nature, a vault-backed perp has no bid and no ask by nature, and under the old rule
            // neither could be recorded at all. A missing figure is now a NULL in its own column and
            // the rest of the row stands.
            //
            // NaN is normalised to NULL rather than trusted through. Adapters signalled "not given"
            // as NaN while that was the only channel for it, and Postgres accepts NaN in a double
            // precision column perfectly happily — so a stray one would now be STORED, a value we
            // never observed sitting where the absence should be. That is the exact failure the old
            // skip existed to prevent, and it survives here as one line instead of a dropped row.
            if (Figures.Absent(t.LastPrice) || Figures.Absent(t.BidPrice) || Figures.Absent(t.AskPrice)
                || Figures.Absent(t.MarkPrice) || Figures.Absent(t.IndexPrice) || Figures.Absent(t.FundingRate)
                || Figures.Absent(t.Turnover24h) || Figures.Absent(t.OpenInterest))
            {
                partial++;
            }

            ids_.Add(id);
            receivedAt.Add(t.ReceivedAt);
            last.Add(Figures.Num(t.LastPrice));
            bid.Add(Figures.Num(t.BidPrice));
            ask.Add(Figures.Num(t.AskPrice));
            bidSize.Add(Figures.Num(t.BidSize));
            askSize.Add(Figures.Num(t.AskSize));
            mark.Add(Figures.Num(t.MarkPrice));
            index.Add(Figures.Num(t.IndexPrice));
            funding.Add(Figures.Num(t.FundingRate));
            turnover.Add(Figures.Num(t.Turnover24h));
            oi.Add(Figures.Num(t.OpenInterest));
            oiAt.Add(t.OpenInterestAt);
            d10b.Add(t.Depth?.Bid10Bps);
            d10a.Add(t.Depth?.Ask10Bps);
            d25b.Add(t.Depth?.Bid25Bps);
            d25a.Add(t.Depth?.Ask25Bps);
            d50b.Add(t.Depth?.Bid50Bps);
            d50a.Add(t.Depth?.Ask50Bps);
            dAt.Add(t.Depth?.At);
            venueTs.Add(t.VenueTs);
            lastTradeAt.Add(t.LastTradeAt);
            fundingPredicted.Add(t.FundingRatePredicted);
            nextFundingAt.Add(t.NextFundingAt);
            volume24hBase.Add(t.Volume24hBase);
            oiQuote.Add(Figures.Num(t.OiQuote));
            marketOpen.Add(t.MarketOpen);
            depthRef.Add(t.Depth?.Mid);
            reachBid.Add(t.Depth?.ReachBidBps);
            reachAsk.Add(t.Depth?.ReachAskBps);
        }

        var written = ids_.Count;
        var unchanged = 0;
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (written > 0)
        {
            void Bind(NpgsqlCommand cmd)
            {
                cmd.Parameters.AddWithValue("ids", ids_.ToArray());
                cmd.Parameters.AddWithValue("received_at", receivedAt.ToArray());
                cmd.Parameters.AddWithValue("last_price", last.ToArray());
                cmd.Parameters.AddWithValue("bid_price", bid.ToArray());
                cmd.Parameters.AddWithValue("ask_price", ask.ToArray());
                cmd.Parameters.AddWithValue("bid_size", bidSize.ToArray());
                cmd.Parameters.AddWithValue("ask_size", askSize.ToArray());
                cmd.Parameters.AddWithValue("mark_price", mark.ToArray());
                cmd.Parameters.AddWithValue("index_price", index.ToArray());
                cmd.Parameters.AddWithValue("funding_rate", funding.ToArray());
                cmd.Parameters.AddWithValue("turnover_24h", turnover.ToArray());
                cmd.Parameters.AddWithValue("open_interest", oi.ToArray());
                cmd.Parameters.AddWithValue("open_interest_at", oiAt.ToArray());
                cmd.Parameters.AddWithValue("d10b", d10b.ToArray());
                cmd.Parameters.AddWithValue("d10a", d10a.ToArray());
                cmd.Parameters.AddWithValue("d25b", d25b.ToArray());
                cmd.Parameters.AddWithValue("d25a", d25a.ToArray());
                cmd.Parameters.AddWithValue("d50b", d50b.ToArray());
                cmd.Parameters.AddWithValue("d50a", d50a.ToArray());
                cmd.Parameters.AddWithValue("d_at", dAt.ToArray());
                cmd.Parameters.AddWithValue("venue_ts", venueTs.ToArray());
                cmd.Parameters.AddWithValue("last_trade_at", lastTradeAt.ToArray());
                cmd.Parameters.AddWithValue("funding_rate_predicted", fundingPredicted.ToArray());
                cmd.Parameters.AddWithValue("next_funding_at", nextFundingAt.ToArray());
                cmd.Parameters.AddWithValue("volume_24h_base", volume24hBase.ToArray());
                cmd.Parameters.AddWithValue("oi_quote", oiQuote.ToArray());
                cmd.Parameters.AddWithValue("market_open", marketOpen.ToArray());
                cmd.Parameters.AddWithValue("depth_ref", depthRef.ToArray());
                cmd.Parameters.AddWithValue("reach_bid", reachBid.ToArray());
                cmd.Parameters.AddWithValue("reach_ask", reachAsk.ToArray());
            }

            // The per-row @HasDepth flag is gone and nothing is lost: it was `t.Depth is not null`,
            // and depth_at is `t.Depth?.At` on a non-nullable field — so "this ticker carried a
            // book" and "depth_at is not null" are the same statement, and the second one is
            // already in the row being inserted.
            await using (var upsert = new NpgsqlCommand(
                """
                insert into market_snapshot_latest (
                    exchange_instrument_id, received_at, last_price, bid_price, ask_price,
                    bid_size, ask_size, mark_price, index_price, funding_rate, turnover_24h,
                    open_interest, open_interest_at,
                    depth_bid_10bps, depth_ask_10bps, depth_bid_25bps, depth_ask_25bps,
                    depth_bid_50bps, depth_ask_50bps, depth_at,
                    venue_ts, last_trade_at, funding_rate_predicted, next_funding_at, volume_24h_base,
                    depth_ref, book_reach_bid, book_reach_ask, oi_quote, market_open)
                select * from unnest(
                    @ids, @received_at, @last_price, @bid_price, @ask_price,
                    @bid_size, @ask_size, @mark_price, @index_price, @funding_rate, @turnover_24h,
                    @open_interest, @open_interest_at,
                    @d10b, @d10a, @d25b, @d25a, @d50b, @d50a, @d_at,
                    @venue_ts, @last_trade_at, @funding_rate_predicted, @next_funding_at, @volume_24h_base,
                    @depth_ref, @reach_bid, @reach_ask, @oi_quote, @market_open)
                on conflict (exchange_instrument_id) do update set
                    received_at      = excluded.received_at,
                    last_price       = excluded.last_price,
                    bid_price        = excluded.bid_price,
                    ask_price        = excluded.ask_price,
                    bid_size         = excluded.bid_size,
                    ask_size         = excluded.ask_size,
                    mark_price       = excluded.mark_price,
                    index_price      = excluded.index_price,
                    funding_rate     = excluded.funding_rate,
                    turnover_24h     = excluded.turnover_24h,
                    open_interest    = excluded.open_interest,
                    open_interest_at = excluded.open_interest_at,
                    -- Depth is only written when this ticker actually carried a book; otherwise the
                    -- existing columns are kept so the depth collector's separate pass is not undone.
                    depth_bid_10bps  = case when excluded.depth_at is not null then excluded.depth_bid_10bps else market_snapshot_latest.depth_bid_10bps end,
                    depth_ask_10bps  = case when excluded.depth_at is not null then excluded.depth_ask_10bps else market_snapshot_latest.depth_ask_10bps end,
                    depth_bid_25bps  = case when excluded.depth_at is not null then excluded.depth_bid_25bps else market_snapshot_latest.depth_bid_25bps end,
                    depth_ask_25bps  = case when excluded.depth_at is not null then excluded.depth_ask_25bps else market_snapshot_latest.depth_ask_25bps end,
                    depth_bid_50bps  = case when excluded.depth_at is not null then excluded.depth_bid_50bps else market_snapshot_latest.depth_bid_50bps end,
                    depth_ask_50bps  = case when excluded.depth_at is not null then excluded.depth_ask_50bps else market_snapshot_latest.depth_ask_50bps end,
                    depth_at         = case when excluded.depth_at is not null then excluded.depth_at else market_snapshot_latest.depth_at end,
                    -- Same rule as the five depth bands above, extended to the two new depth-derived
                    -- columns: only overwritten when THIS pass's ticker actually carried a book, so a
                    -- venue whose depth arrives from a separate out-of-band pass (WEEX, HL) is not
                    -- reset to NULL on every snapshot pass that ran without it.
                    depth_ref        = case when excluded.depth_at is not null then excluded.depth_ref else market_snapshot_latest.depth_ref end,
                    book_reach_bid   = case when excluded.depth_at is not null then excluded.book_reach_bid else market_snapshot_latest.book_reach_bid end,
                    book_reach_ask   = case when excluded.depth_at is not null then excluded.book_reach_ask else market_snapshot_latest.book_reach_ask end,
                    -- Straight from this pass's own ticker, unconditionally — these five are never
                    -- out-of-band; a venue that never carries one always sends NULL for it, and
                    -- overwriting with NULL is the honest answer for that venue.
                    venue_ts               = excluded.venue_ts,
                    last_trade_at          = excluded.last_trade_at,
                    funding_rate_predicted = excluded.funding_rate_predicted,
                    next_funding_at        = excluded.next_funding_at,
                    volume_24h_base        = excluded.volume_24h_base,
                    -- Same rule as the five above: never out of band, and a venue that publishes
                    -- neither always sends NULL, which is the honest answer for that venue.
                    oi_quote               = excluded.oi_quote,
                    market_open            = excluded.market_open
                """,
                conn, tx))
            {
                Bind(upsert);
                await upsert.ExecuteNonQueryAsync(ct);
            }

            if (writeHistory)
            {
                // Depth comes from the latest row, not from this ticker payload. On Kraken the
                // ticker carries a book and the two are the same value; on Hyperliquid and WEEX it
                // does not, and depth arrives only from DepthCollector — which writes the latest
                // row and nothing else. Taking the parameters here wrote nulls for those venues, so
                // their order-book depth was measured every minute and discarded every minute:
                // 1,188 instruments, from the day each adapter went live, in the one category that
                // cannot be re-fetched. The latest rows are upserted immediately above in this same
                // transaction and preserve depth when the ticker has none, so by this point they
                // hold the freshest measurement whichever loop produced it. depth_at travels with
                // it — the column exists precisely because depth runs on its own clock.
                // depth_ref/book_reach_bid/book_reach_ask are read from l (the latest row just
                // upserted above), not from v (this pass's ticker) — same reasoning as the five
                // depth bands already here: on WEEX/HL depth arrives from a separate out-of-band
                // pass, and by this point l holds the freshest measurement whichever loop produced
                // it. venue_ts/last_trade_at/funding_rate_predicted/next_funding_at/volume_24h_base
                // are never out-of-band, so those five come straight from v.
                await using var history = new NpgsqlCommand(
                    """
                    insert into market_snapshot (
                        exchange_instrument_id, received_at, last_price, bid_price, ask_price,
                        bid_size, ask_size, mark_price, index_price, funding_rate, turnover_24h,
                        open_interest, open_interest_at,
                        depth_bid_10bps, depth_ask_10bps, depth_bid_25bps, depth_ask_25bps,
                        depth_bid_50bps, depth_ask_50bps, depth_at,
                        venue_ts, last_trade_at, funding_rate_predicted, next_funding_at, volume_24h_base,
                        depth_ref, book_reach_bid, book_reach_ask, oi_quote, market_open)
                    select
                        v.id, v.received_at, v.last_price, v.bid_price, v.ask_price,
                        v.bid_size, v.ask_size, v.mark_price, v.index_price, v.funding_rate,
                        v.turnover_24h, v.open_interest, v.open_interest_at,
                        l.depth_bid_10bps, l.depth_ask_10bps, l.depth_bid_25bps, l.depth_ask_25bps,
                        l.depth_bid_50bps, l.depth_ask_50bps, l.depth_at,
                        v.venue_ts, v.last_trade_at, v.funding_rate_predicted, v.next_funding_at, v.volume_24h_base,
                        l.depth_ref, l.book_reach_bid, l.book_reach_ask, v.oi_quote, v.market_open
                      from unnest(
                            @ids, @received_at, @last_price, @bid_price, @ask_price,
                            @bid_size, @ask_size, @mark_price, @index_price, @funding_rate,
                            @turnover_24h, @open_interest, @open_interest_at,
                            @d10b, @d10a, @d25b, @d25a, @d50b, @d50a, @d_at,
                            @venue_ts, @last_trade_at, @funding_rate_predicted, @next_funding_at, @volume_24h_base,
                            @depth_ref, @reach_bid, @reach_ask, @oi_quote, @market_open)
                            as v(id, received_at, last_price, bid_price, ask_price,
                                 bid_size, ask_size, mark_price, index_price, funding_rate,
                                 turnover_24h, open_interest, open_interest_at,
                                 d10b, d10a, d25b, d25a, d50b, d50a, d_at,
                                 venue_ts, last_trade_at, funding_rate_predicted, next_funding_at, volume_24h_base,
                                 depth_ref, reach_bid, reach_ask, oi_quote, market_open)
                      join market_snapshot_latest l on l.exchange_instrument_id = v.id
                    on conflict (exchange_instrument_id, received_at) do nothing
                    """,
                    conn, tx);

                Bind(history);

                // The insert is keyed on (instrument, received_at), and received_at is the VENUE's
                // clock on Kraken (both the WS feed and the REST server_time), not ours. A cached
                // WS record is served unchanged for up to ws_stale_after_s, so an instrument the
                // venue has not re-published can present the same instant to two keep passes and
                // the second one no-ops. Counted rather than discarded: the only other trace it
                // leaves is snapshot_count below expected_count with no gap, which the reader is
                // told to interpret as a quiet market.
                //
                // Counted by subtraction now that the write is one statement — the per-row return
                // value is gone, but "how many of the rows we offered did not land" is the same
                // number and is what the message below actually says.
                unchanged = written - await history.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        if (writeHistory)
        {
            _lastHistoryBucket = bucket;
            _lastHistoryIntervalS = historyInterval;
        }

        if (unchanged > 0)
        {
            _logger.LogWarning(
                "{Exchange}/snapshot kept nothing for {Unchanged} instruments — the venue re-served an "
                + "observation this instrument already has at that instant, so the keep interval of "
                + "{HistoryInterval}s is finer than this venue's own clock resolution",
                _adapter.SegmentCode, unchanged, historyInterval);
        }

        if (partial > 0)
        {
            // Loud on purpose: a venue that stopped sending a field looks exactly like a quiet
            // market in the row count, and only this line says which it was.
            _logger.LogWarning(
                "{Exchange}/snapshot wrote {Partial} instruments whose ticker was missing a figure",
                _adapter.SegmentCode, partial);
        }

        return written;
    }
}

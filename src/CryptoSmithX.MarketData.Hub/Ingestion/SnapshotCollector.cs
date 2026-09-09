using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Writes the current state of every instrument. <c>market_snapshot_latest</c> is upserted on every
/// pass; the same rows are appended to the history every <c>history_interval_s</c> seconds, which
/// resolves per segment×dataset cell (0020).
/// A row goes in whole or not at all — a half-written row would hide staleness behind a fresh
/// received_at, and an observation missing a field is skipped rather than completed with a zero.
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
        var skipped = 0;

        var ids_ = new List<int>(tickers.Count);
        var receivedAt = new List<DateTimeOffset>(tickers.Count);
        var last = new List<double>(tickers.Count);
        var bid = new List<double>(tickers.Count);
        var ask = new List<double>(tickers.Count);
        var bidSize = new List<double>(tickers.Count);
        var askSize = new List<double>(tickers.Count);
        var mark = new List<double>(tickers.Count);
        var index = new List<double>(tickers.Count);
        var funding = new List<double>(tickers.Count);
        var turnover = new List<double>(tickers.Count);
        var oi = new List<double>(tickers.Count);
        var oiAt = new List<DateTimeOffset>(tickers.Count);
        var d10b = new List<double?>(tickers.Count);
        var d10a = new List<double?>(tickers.Count);
        var d25b = new List<double?>(tickers.Count);
        var d25a = new List<double?>(tickers.Count);
        var d50b = new List<double?>(tickers.Count);
        var d50a = new List<double?>(tickers.Count);
        var dAt = new List<DateTimeOffset?>(tickers.Count);

        foreach (var t in tickers)
        {
            if (!ids.TryGetValue(t.ExchangeSymbol, out var id))
            {
                // Seen by the ticker call but not yet by discovery; it will exist next round.
                continue;
            }

            // An adapter signals "the venue did not give me this number" as NaN, because the columns
            // here are NOT NULL and a row is written whole or not at all. Writing it anyway would
            // store a value we never observed, which is the one thing this system must not do — so
            // the observation is skipped and the minute simply has no row for this instrument. That
            // absence is honest; a zero would not be.
            if (double.IsNaN(t.LastPrice) || double.IsNaN(t.BidPrice) || double.IsNaN(t.AskPrice)
                || double.IsNaN(t.MarkPrice) || double.IsNaN(t.IndexPrice) || double.IsNaN(t.FundingRate)
                || double.IsNaN(t.Turnover24h) || double.IsNaN(t.OpenInterest))
            {
                skipped++;
                continue;
            }

            ids_.Add(id);
            receivedAt.Add(t.ReceivedAt);
            last.Add(t.LastPrice);
            bid.Add(t.BidPrice);
            ask.Add(t.AskPrice);
            bidSize.Add(t.BidSize);
            askSize.Add(t.AskSize);
            mark.Add(t.MarkPrice);
            index.Add(t.IndexPrice);
            funding.Add(t.FundingRate);
            turnover.Add(t.Turnover24h);
            oi.Add(t.OpenInterest);
            oiAt.Add(t.OpenInterestAt);
            d10b.Add(t.Depth?.Bid10Bps);
            d10a.Add(t.Depth?.Ask10Bps);
            d25b.Add(t.Depth?.Bid25Bps);
            d25a.Add(t.Depth?.Ask25Bps);
            d50b.Add(t.Depth?.Bid50Bps);
            d50a.Add(t.Depth?.Ask50Bps);
            dAt.Add(t.Depth?.At);
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
                    depth_bid_50bps, depth_ask_50bps, depth_at)
                select * from unnest(
                    @ids, @received_at, @last_price, @bid_price, @ask_price,
                    @bid_size, @ask_size, @mark_price, @index_price, @funding_rate, @turnover_24h,
                    @open_interest, @open_interest_at,
                    @d10b, @d10a, @d25b, @d25a, @d50b, @d50a, @d_at)
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
                    depth_at         = case when excluded.depth_at is not null then excluded.depth_at else market_snapshot_latest.depth_at end
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
                await using var history = new NpgsqlCommand(
                    """
                    insert into market_snapshot (
                        exchange_instrument_id, received_at, last_price, bid_price, ask_price,
                        bid_size, ask_size, mark_price, index_price, funding_rate, turnover_24h,
                        open_interest, open_interest_at,
                        depth_bid_10bps, depth_ask_10bps, depth_bid_25bps, depth_ask_25bps,
                        depth_bid_50bps, depth_ask_50bps, depth_at)
                    select
                        v.id, v.received_at, v.last_price, v.bid_price, v.ask_price,
                        v.bid_size, v.ask_size, v.mark_price, v.index_price, v.funding_rate,
                        v.turnover_24h, v.open_interest, v.open_interest_at,
                        l.depth_bid_10bps, l.depth_ask_10bps, l.depth_bid_25bps, l.depth_ask_25bps,
                        l.depth_bid_50bps, l.depth_ask_50bps, l.depth_at
                      from unnest(
                            @ids, @received_at, @last_price, @bid_price, @ask_price,
                            @bid_size, @ask_size, @mark_price, @index_price, @funding_rate,
                            @turnover_24h, @open_interest, @open_interest_at,
                            @d10b, @d10a, @d25b, @d25a, @d50b, @d50a, @d_at)
                            as v(id, received_at, last_price, bid_price, ask_price,
                                 bid_size, ask_size, mark_price, index_price, funding_rate,
                                 turnover_24h, open_interest, open_interest_at,
                                 d10b, d10a, d25b, d25a, d50b, d50a, d_at)
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

        if (skipped > 0)
        {
            // Loud on purpose: a venue that stopped sending a field looks exactly like a quiet
            // market in the row count, and only this line says which it was.
            _logger.LogWarning(
                "{Exchange}/snapshot skipped {Skipped} instruments whose ticker was missing a required field",
                _adapter.SegmentCode, skipped);
        }

        return written;
    }
}

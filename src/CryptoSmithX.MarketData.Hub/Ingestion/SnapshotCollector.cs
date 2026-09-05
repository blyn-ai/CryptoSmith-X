using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.Database;
using Dapper;

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

        var written = 0;
        var skipped = 0;
        var unchanged = 0;
        await using var tx = await conn.BeginTransactionAsync(ct);

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

            var row = new
            {
                Id = id,
                // A ticker without a book must not erase depth the depth collector wrote on its own,
                // slower pass; this flag keeps those columns as they are when there is nothing new.
                HasDepth = t.Depth is not null,
                t.ReceivedAt,
                t.LastPrice,
                t.BidPrice,
                t.AskPrice,
                t.BidSize,
                t.AskSize,
                t.MarkPrice,
                t.IndexPrice,
                t.FundingRate,
                t.Turnover24h,
                t.OpenInterest,
                t.OpenInterestAt,
                DepthBid10 = t.Depth?.Bid10Bps,
                DepthAsk10 = t.Depth?.Ask10Bps,
                DepthBid25 = t.Depth?.Bid25Bps,
                DepthAsk25 = t.Depth?.Ask25Bps,
                DepthBid50 = t.Depth?.Bid50Bps,
                DepthAsk50 = t.Depth?.Ask50Bps,
                DepthAt = t.Depth?.At,
            };

            await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into market_snapshot_latest (
                    exchange_instrument_id, received_at, last_price, bid_price, ask_price,
                    bid_size, ask_size, mark_price, index_price, funding_rate, turnover_24h,
                    open_interest, open_interest_at,
                    depth_bid_10bps, depth_ask_10bps, depth_bid_25bps, depth_ask_25bps,
                    depth_bid_50bps, depth_ask_50bps, depth_at)
                values (
                    @Id, @ReceivedAt, @LastPrice, @BidPrice, @AskPrice,
                    @BidSize, @AskSize, @MarkPrice, @IndexPrice, @FundingRate, @Turnover24h,
                    @OpenInterest, @OpenInterestAt,
                    @DepthBid10, @DepthAsk10, @DepthBid25, @DepthAsk25,
                    @DepthBid50, @DepthAsk50, @DepthAt)
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
                    depth_bid_10bps  = case when @HasDepth then excluded.depth_bid_10bps else market_snapshot_latest.depth_bid_10bps end,
                    depth_ask_10bps  = case when @HasDepth then excluded.depth_ask_10bps else market_snapshot_latest.depth_ask_10bps end,
                    depth_bid_25bps  = case when @HasDepth then excluded.depth_bid_25bps else market_snapshot_latest.depth_bid_25bps end,
                    depth_ask_25bps  = case when @HasDepth then excluded.depth_ask_25bps else market_snapshot_latest.depth_ask_25bps end,
                    depth_bid_50bps  = case when @HasDepth then excluded.depth_bid_50bps else market_snapshot_latest.depth_bid_50bps end,
                    depth_ask_50bps  = case when @HasDepth then excluded.depth_ask_50bps else market_snapshot_latest.depth_ask_50bps end,
                    depth_at         = case when @HasDepth then excluded.depth_at else market_snapshot_latest.depth_at end
                """,
                row, tx, cancellationToken: ct));

            if (writeHistory)
            {
                // Depth comes from the latest row, not from this ticker payload. On Kraken the
                // ticker carries a book and the two are the same value; on Hyperliquid and WEEX it
                // does not, and depth arrives only from DepthCollector — which writes the latest
                // row and nothing else. Taking the parameters here wrote nulls for those venues, so
                // their order-book depth was measured every minute and discarded every minute:
                // 1,188 instruments, from the day each adapter went live, in the one category that
                // cannot be re-fetched. The latest row is upserted immediately above in this same
                // transaction and preserves depth when the ticker has none, so by this point it
                // holds the freshest measurement whichever loop produced it. depth_at travels with
                // it — the column exists precisely because depth runs on its own clock.
                if (await conn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into market_snapshot (
                        exchange_instrument_id, received_at, last_price, bid_price, ask_price,
                        bid_size, ask_size, mark_price, index_price, funding_rate, turnover_24h,
                        open_interest, open_interest_at,
                        depth_bid_10bps, depth_ask_10bps, depth_bid_25bps, depth_ask_25bps,
                        depth_bid_50bps, depth_ask_50bps, depth_at)
                    select
                        @Id, @ReceivedAt, @LastPrice, @BidPrice, @AskPrice,
                        @BidSize, @AskSize, @MarkPrice, @IndexPrice, @FundingRate, @Turnover24h,
                        @OpenInterest, @OpenInterestAt,
                        l.depth_bid_10bps, l.depth_ask_10bps, l.depth_bid_25bps, l.depth_ask_25bps,
                        l.depth_bid_50bps, l.depth_ask_50bps, l.depth_at
                      from market_snapshot_latest l
                     where l.exchange_instrument_id = @Id
                    on conflict (exchange_instrument_id, received_at) do nothing
                    """,
                    row, tx, cancellationToken: ct)) == 0)
                {
                    // The insert is keyed on (instrument, received_at), and received_at is the
                    // VENUE's clock on Kraken (both the WS feed and the REST server_time), not
                    // ours. A cached WS record is served unchanged for up to ws_stale_after_s, so
                    // an instrument the venue has not re-published can present the same instant to
                    // two keep passes and the second one no-ops. That used to be impossible by
                    // accident — 60 s of keeping against 30 s of staleness — and stopped being
                    // impossible the moment keeping became a per-cell number that an operator can
                    // set to 10. Counted rather than discarded: the only other trace it leaves is
                    // snapshot_count below expected_count with no gap, which the reader is told to
                    // interpret as a quiet market.
                    unchanged++;
                }
            }

            written++;
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

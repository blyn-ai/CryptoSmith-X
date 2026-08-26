using CryptoSmithX.Exchanges;
using CryptoSmithX.MarketData.Storage;
using Dapper;

namespace CryptoSmithX.MarketData.Ingestion;

/// <summary>
/// Writes the current state of every instrument. <c>market_snapshot_latest</c> is upserted on every
/// pass; the same rows are appended to the history once a minute. A row goes in whole or not at
/// all — a half-written row would hide staleness behind a fresh received_at.
/// </summary>
public sealed class SnapshotCollector
{
    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private long _lastHistoryMinute = -1;

    public SnapshotCollector(IExchangeMarketData adapter, Db db, TimeProvider clock)
    {
        _adapter = adapter;
        _db = db;
        _clock = clock;
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
                "select exchange_symbol, id from exchange_instrument where exchange_code = @code",
                new { code = _adapter.ExchangeCode },
                cancellationToken: ct)))
            .ToDictionary(r => r.Symbol, r => r.Id, StringComparer.Ordinal);

        var minute = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var writeHistory = minute != _lastHistoryMinute;
        if (writeHistory)
        {
            await Partitions.EnsureAsync(conn, _clock.GetUtcNow(), ct);
        }

        var written = 0;
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var t in tickers)
        {
            if (!ids.TryGetValue(t.ExchangeSymbol, out var id))
            {
                // Seen by the ticker call but not yet by discovery; it will exist next round.
                continue;
            }

            var row = new
            {
                Id = id,
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
                    depth_bid_10bps  = excluded.depth_bid_10bps,
                    depth_ask_10bps  = excluded.depth_ask_10bps,
                    depth_bid_25bps  = excluded.depth_bid_25bps,
                    depth_ask_25bps  = excluded.depth_ask_25bps,
                    depth_bid_50bps  = excluded.depth_bid_50bps,
                    depth_ask_50bps  = excluded.depth_ask_50bps,
                    depth_at         = excluded.depth_at
                """,
                row, tx, cancellationToken: ct));

            if (writeHistory)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    insert into market_snapshot (
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
                    on conflict (exchange_instrument_id, received_at) do nothing
                    """,
                    row, tx, cancellationToken: ct));
            }

            written++;
        }

        await tx.CommitAsync(ct);
        if (writeHistory)
        {
            _lastHistoryMinute = minute;
        }

        return written;
    }
}

using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Fills the order-book depth on <c>market_snapshot_latest</c>. Unlike the rest of the snapshot the
/// book is a per-symbol call, so it runs as its own slower loop (0001 always reserved a 'depth'
/// collector) and only for instruments the venue lists as <c>trading</c>. A symbol not reached
/// within a pass keeps its previous depth and its honestly older <c>depth_at</c> — the snapshot
/// writer leaves depth untouched whenever a ticker carries no book, so the two never fight.
/// Adapters that carry the book inline in the ticker (the fake) return null here and this no-ops.
/// </summary>
public sealed class DepthCollector
{
    // Kraken Futures /derivatives allows 500 cost-units per 10 s and exempts public endpoints from
    // the budget entirely (docs.kraken.com/api/docs/guides/futures-rate-limits). We still pace: a
    // 50 ms gap is ~20 req/s, so a full 316-symbol pass takes ~16 s — inside the 60 s depth interval
    // and well under the budget, leaving room for the discovery/snapshot/funding loops on the same IP.
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);

    // Only trading instruments have a book worth measuring, and only ones an operator left collect on.
    internal const string TargetInstrumentsSql =
        """
        select id, exchange_symbol
          from exchange_instrument
         where segment_code = @code and collect = true and status = 'trading'
         order by exchange_symbol
        """;

    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;
    private readonly TimeProvider _clock;

    public DepthCollector(IExchangeMarketData adapter, Db db, TimeProvider clock)
    {
        _adapter = adapter;
        _db = db;
        _clock = clock;
    }

    /// <summary>Returns the number of instruments whose depth was refreshed this pass.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        var targets = (await conn.QueryAsync<(int Id, string Symbol)>(new CommandDefinition(
            TargetInstrumentsSql,
            new { code = _adapter.SegmentCode },
            cancellationToken: ct))).ToList();

        var written = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, symbol) = targets[i];

            var depth = await _adapter.GetOrderBookAsync(symbol, ct);
            if (depth is not null)
            {
                // Update only the depth columns; the row itself is owned by the snapshot writer, and
                // affects zero rows harmlessly until the first snapshot for this instrument lands.
                written += await conn.ExecuteAsync(new CommandDefinition(
                    """
                    update market_snapshot_latest set
                        depth_bid_10bps = @Bid10, depth_ask_10bps = @Ask10,
                        depth_bid_25bps = @Bid25, depth_ask_25bps = @Ask25,
                        depth_bid_50bps = @Bid50, depth_ask_50bps = @Ask50,
                        depth_at        = @At
                     where exchange_instrument_id = @Id
                    """,
                    new
                    {
                        Id = id,
                        Bid10 = depth.Bid10Bps,
                        Ask10 = depth.Ask10Bps,
                        Bid25 = depth.Bid25Bps,
                        Ask25 = depth.Ask25Bps,
                        Bid50 = depth.Bid50Bps,
                        Ask50 = depth.Ask50Bps,
                        depth.At,
                    },
                    cancellationToken: ct));
            }

            // Pace between calls, not after the last one.
            if (i < targets.Count - 1)
            {
                await Task.Delay(Pause, _clock, ct);
            }
        }

        return written;
    }
}

using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Live;

/// <summary>
/// <c>(segment, exchange symbol) ↔ instrument id</c>, for the live egress and nothing else.
///
/// <b>Why the id and not the symbol is the key on the wire.</b> A venue's spelling is the venue's
/// business — Kraken says PF_XBTUSD where Hyperliquid says BTC — and the page asking for a frame
/// knows only what the database calls the listing. Translating at the edge means the studio never
/// learns a venue's alphabet and a symbol change is one venue's problem.
///
/// The predicate is the collectors' own, <c>collect = true and status = 'trading'</c>: an
/// instrument nobody records is an instrument nothing on the page can show a written age beside,
/// so streaming it live would be a figure with no counterpart. The SQL is a constant so
/// <c>CollectFilterTests</c>'s convention — prove the clause without a database — applies here too.
///
/// Re-read every 60 s rather than cached for the process's life, for the same reason
/// <c>ExchangeWorker.CollectedSymbols</c> re-reads: switching an instrument on in the console must
/// reach the live path without a restart. Sixty seconds is the delay that buys, and it is a whole
/// minute later than the feed's own subscription changes — which is why a symbol the map does not
/// know yet is skipped silently rather than logged as an error.
/// </summary>
public sealed class InstrumentMap
{
    // Aliased to the record's own names, quoted, the way CandleStore and every other Dapper query
    // here does it. Two reasons, and the first one shipped broken once: the column is
    // exchange_instrument.id — there is no instrument_id column on this table, only on the tables
    // that point at it. And Dapper is not configured to match names across underscores anywhere in
    // this solution, so a bare snake_case column would not bind to a PascalCase member even if the
    // name existed.
    public const string TargetInstrumentsSql =
        """
        select id              as "InstrumentId",
               segment_code    as "SegmentCode",
               exchange_symbol as "ExchangeSymbol"
          from exchange_instrument
         where collect = true
           and status = 'trading'
        """;

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(60);

    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    private volatile Snapshot _current = Snapshot.Empty;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public InstrumentMap(Db db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The map as of the last read, refreshing it first when that read has aged out. One
    /// refresh at a time; a caller arriving mid-refresh gets the previous map rather than a second
    /// query, which is the same single-flight discipline <c>StudioCache</c> uses.</summary>
    public async Task<Snapshot> CurrentAsync(CancellationToken ct)
    {
        if (_clock.GetUtcNow() - _loadedAt < RefreshAfter)
        {
            return _current;
        }

        if (!await _refreshing.WaitAsync(0, ct))
        {
            return _current;
        }

        try
        {
            await using var conn = await _db.OpenAsync(ct);
            var rows = await conn.QueryAsync<Row>(new CommandDefinition(TargetInstrumentsSql, cancellationToken: ct));
            _current = Snapshot.Of(rows);
            _loadedAt = _clock.GetUtcNow();
        }
        finally
        {
            _refreshing.Release();
        }

        return _current;
    }

    /// <summary>Internal rather than private so a test can build a <see cref="Snapshot"/> without a
    /// database — the map's lookups are worth proving, and the query behind them is proven the way
    /// <c>CollectFilterTests</c> proves every other collector's: on the SQL constant.</summary>
    internal sealed record Row(int InstrumentId, string SegmentCode, string ExchangeSymbol);

    /// <summary>One reading of the map, immutable so a refresh replaces it whole rather than
    /// mutating one an egress loop is walking.</summary>
    public sealed class Snapshot
    {
        public static readonly Snapshot Empty = new([], []);

        private readonly Dictionary<(string Segment, string Symbol), int> _toId;
        private readonly Dictionary<int, (string Segment, string Symbol)> _fromId;

        private Snapshot(
            Dictionary<(string, string), int> toId,
            Dictionary<int, (string, string)> fromId)
        {
            _toId = toId;
            _fromId = fromId;
        }

        internal static Snapshot Of(IEnumerable<Row> rows)
        {
            var toId = new Dictionary<(string, string), int>();
            var fromId = new Dictionary<int, (string, string)>();
            foreach (var r in rows)
            {
                toId[(r.SegmentCode, r.ExchangeSymbol)] = r.InstrumentId;
                fromId[r.InstrumentId] = (r.SegmentCode, r.ExchangeSymbol);
            }

            return new Snapshot(toId, fromId);
        }

        public int Count => _fromId.Count;

        public bool TryGetId(string segmentCode, string exchangeSymbol, out int instrumentId) =>
            _toId.TryGetValue((segmentCode, exchangeSymbol), out instrumentId);

        public bool TryGetSegment(int instrumentId, out string segmentCode)
        {
            if (_fromId.TryGetValue(instrumentId, out var pair))
            {
                segmentCode = pair.Segment;
                return true;
            }

            segmentCode = string.Empty;
            return false;
        }

        /// <summary>The segments the wanted ids fall across — what the egress polls, so a
        /// connection watching one asset never walks the venues it is not asking about.</summary>
        public IReadOnlyCollection<string> SegmentsOf(IEnumerable<int> instrumentIds)
        {
            var segments = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in instrumentIds)
            {
                if (_fromId.TryGetValue(id, out var pair))
                {
                    segments.Add(pair.Segment);
                }
            }

            return segments;
        }
    }
}

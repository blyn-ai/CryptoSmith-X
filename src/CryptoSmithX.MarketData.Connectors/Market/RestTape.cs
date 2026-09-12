using System.Collections.Concurrent;

namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// A trade tape assembled by POLLING rather than by subscribing, for the venues whose public tape is
/// a REST route.
///
/// <b>Why a shared class.</b> Five venues need the same three things — remember what has already
/// been seen, hand the collector only what is new, and never grow without bound — and five copies of
/// that is five chances to get the dedup subtly wrong in a way no test on any one venue would catch.
///
/// <b>What honest means here.</b> A polled tape is complete only while the poll is faster than the
/// market. Each adapter states its own page size and cadence beside its call, and the identity below
/// is what makes a re-read of overlapping pages harmless rather than a double count. Where the two
/// cannot keep up — a venue whose busiest instrument prints more in one interval than a page holds —
/// that is a property of the venue's route and belongs in that adapter's own remarks, not hidden
/// here.
/// </summary>
public sealed class RestTape
{
    /// <summary>How many identities to remember per symbol. Generous against any page size a venue
    /// offers (the largest in use is a thousand), so an overlapping re-read is always recognised;
    /// bounded so a process that runs for weeks does not accumulate one entry per trade ever seen.</summary>
    private const int Remembered = 4_000;

    /// <summary>
    /// How far back a print handed to <see cref="Observe"/> may be stamped.
    ///
    /// A "recent trades" route on a DELISTED pair answers with the last prints it ever had, which
    /// can be months old — the pair stopped trading, the route did not stop answering. Those are
    /// real trades, but they are not recent ones, and storing them writes rows outside the range the
    /// store keeps partitions for: one such symbol failed the whole batch it travelled in, taking
    /// every live pair's tape down with it. Seen once on Avantis and again on Gate.
    ///
    /// Generous enough that a collector catching up after an outage still stores what it polls.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(2);

    /// <summary>The other side of the same guard: a venue whose clock runs ahead, or a field read as
    /// the wrong unit, lands in a future no partition covers either.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, SymbolSeen> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<TradeEvent> _pending = new();

    /// <summary>
    /// Folds one poll's worth of tape in, keeping only what has not been handed on before.
    ///
    /// <paramref name="trades"/> may arrive in either direction — venues disagree, and several
    /// return newest first — so nothing here depends on the order.
    /// </summary>
    public void Observe(string exchangeSymbol, IEnumerable<TradeEvent> trades)
    {
        var seen = _seen.GetOrAdd(exchangeSymbol, _ => new SymbolSeen());
        var now = DateTimeOffset.UtcNow;

        foreach (var trade in trades)
        {
            if (!Quantified(trade))
            {
                // Not remembered: an entry with no quantity is not an execution this tape has seen,
                // and a later poll that reports a real size for the same id must still get through.
                continue;
            }

            if (trade.EventTime < now - Window || trade.EventTime > now + Ahead)
            {
                // Still marked as seen, so a delisted pair's unchanging page is not re-examined
                // print by print on every poll for as long as the process lives.
                seen.Add(trade.VenueUid);
                continue;
            }

            if (seen.Add(trade.VenueUid))
            {
                _pending.Enqueue(trade);
            }
        }
    }

    /// <summary>
    /// Whether this entry states an executed quantity at a price.
    ///
    /// Gate's tape carries entries with <c>"size": 0</c> — thirteen of a hundred on PEPE_USDT,
    /// measured, beside a price and an id like any other row. Whatever they record, it is not a
    /// quantity that changed hands, and the store says so in its own words: the trade table checks
    /// <c>qty > 0</c>, and one such row fails the entire batch it travelled in, so a handful of them
    /// on one symbol cost every symbol's tape for that pass.
    ///
    /// Safe to decide centrally, unlike a timestamp: no misread scale turns a real size into zero,
    /// so this cannot quietly swallow a unit bug the way a window guard can.
    /// </summary>
    private static bool Quantified(TradeEvent trade) =>
        double.IsFinite(trade.Qty) && trade.Qty > 0
        && double.IsFinite(trade.Price) && trade.Price > 0;

    /// <summary>Everything new since the last call, and the buffer is emptied by it — the same
    /// contract a socket-fed adapter's own drain has.</summary>
    public IReadOnlyList<TradeEvent> Drain()
    {
        var drained = new List<TradeEvent>();
        while (_pending.TryDequeue(out var trade))
        {
            drained.Add(trade);
        }

        return drained;
    }

    /// <summary>The newest print seen for one symbol, which is what the Last and Last-trade columns
    /// hold. Kept per symbol rather than derived from the queue, because the queue is emptied by
    /// the collector and this fact outlives that.</summary>
    public (double Price, DateTimeOffset At)? Last(string exchangeSymbol) =>
        _seen.TryGetValue(exchangeSymbol, out var s) ? s.Last : null;

    private sealed class SymbolSeen
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private readonly Lock _gate = new();

        public (double Price, DateTimeOffset At)? Last { get; private set; }

        public bool Add(string uid)
        {
            lock (_gate)
            {
                if (!_ids.Add(uid))
                {
                    return false;
                }

                // FIFO eviction rather than a periodic clear: clearing would let a trade still
                // inside the venue's own page be counted a second time on the very next poll, which
                // is exactly the case this class exists to prevent.
                _order.Enqueue(uid);
                while (_order.Count > Remembered)
                {
                    _ids.Remove(_order.Dequeue());
                }

                return true;
            }
        }

        public void Saw(double price, DateTimeOffset at)
        {
            lock (_gate)
            {
                if (Last is not { } last || at >= last.At)
                {
                    Last = (price, at);
                }
            }
        }
    }

    /// <summary>Records a print as the symbol's newest if it is. Separate from
    /// <see cref="Observe"/>'s dedup because the last price is true whether or not the trade was new
    /// to us — a restart must not leave the column empty until the next print arrives.</summary>
    public void Saw(string exchangeSymbol, double price, DateTimeOffset at) =>
        _seen.GetOrAdd(exchangeSymbol, _ => new SymbolSeen()).Saw(price, at);
}

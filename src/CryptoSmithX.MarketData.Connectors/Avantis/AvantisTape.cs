using System.Collections.Concurrent;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// The venue's own public trade tape, kept as a rolling day.
///
/// <b>Why this class exists at all.</b> An earlier audit concluded that Avantis publishes no
/// executions, and from that concluded that Last, Turnover and Liquidations could never be filled
/// here. The conclusion was drawn from one endpoint. <c>GET /v1/history/recent-trades/{pairIndex}</c>
/// is public, market-wide and anonymous, and it carries price, size, timestamp, the transaction hash
/// and an <c>isLiquidation</c> flag. Everything those three columns need is on it.
///
/// <b>Why a window rather than a fetch.</b> The endpoint answers with the last ten trades — and on
/// this venue ten trades span hours (measured: 4 h 01 on ETH, 1 h 47 on BTC, 5 h 51 on a thin pair),
/// so a once-a-minute poll cannot miss one. What it cannot do is answer "how much traded in the last
/// twenty-four hours", because that is not a thing the venue publishes. So the window is
/// accumulated here from the tape we are reading anyway, deduplicated by the trade's own identity.
///
/// <b>What that costs the reader, stated plainly.</b> A window built by watching is only as old as
/// the watching. After a restart the figure is a partial day and says so through its own
/// <see cref="Since"/>; it is not presented as a full day until it has been one. That is the honest
/// version of a number no endpoint hands over.
/// </summary>
public sealed class AvantisTape
{
    /// <summary>The window the Turnover and Liquidations columns are defined over. Same twenty-four
    /// hours every other venue's own figure covers, so the column stays a comparison.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, SymbolTape> _bySymbol = new(StringComparer.Ordinal);

    /// <summary>
    /// Folds one poll's worth of tape in and reports what was new.
    ///
    /// Identity is the venue's own: the transaction hash paired with the record id, because one
    /// transaction can settle more than one position and the hash alone would collapse them.
    /// </summary>
    internal IReadOnlyList<TradeEvent> Observe(
        string exchangeSymbol, IReadOnlyList<AvTrade> trades, DateTimeOffset now)
    {
        var tape = _bySymbol.GetOrAdd(exchangeSymbol, _ => new SymbolTape(now));
        return tape.Observe(exchangeSymbol, trades, now);
    }

    /// <summary>Rolling notional in the quote asset, or null until anything has been seen. Null
    /// rather than zero: "nothing has traded" and "we have not looked yet" are different facts, and
    /// only one of them belongs in a column.</summary>
    public double? Turnover(string exchangeSymbol, DateTimeOffset now) =>
        _bySymbol.TryGetValue(exchangeSymbol, out var t) ? t.Sum(now, liquidationsOnly: false) : null;

    /// <summary>The same window, counting only the trades the venue itself marked as
    /// liquidations.</summary>
    public double? Liquidations(string exchangeSymbol, DateTimeOffset now) =>
        _bySymbol.TryGetValue(exchangeSymbol, out var t) ? t.Sum(now, liquidationsOnly: true) : null;

    /// <summary>The newest trade seen, which is what the Last and Last-trade columns hold.</summary>
    public (double Price, DateTimeOffset At)? Last(string exchangeSymbol) =>
        _bySymbol.TryGetValue(exchangeSymbol, out var t) ? t.Last : null;

    /// <summary>When this symbol's window started being watched. A figure whose window is younger
    /// than a day is a partial sum, and the reader is owed that.</summary>
    public DateTimeOffset? Since(string exchangeSymbol) =>
        _bySymbol.TryGetValue(exchangeSymbol, out var t) ? t.Since : null;

    /// <summary>Liquidation events in [from, to] as buckets, for the dataset that keeps their
    /// history. Bucketed by the caller's interval from the same window the column reads, so the two
    /// can never disagree about what happened.</summary>
    public IReadOnlyList<LiquidationBucket> LiquidationBuckets(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, int intervalSeconds, string unit) =>
        _bySymbol.TryGetValue(exchangeSymbol, out var t)
            ? t.Buckets(exchangeSymbol, from, to, intervalSeconds, unit)
            : [];

    private sealed class SymbolTape
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<(string Uid, DateTimeOffset At, double Notional, double Price, bool Liquidation)> _rows = [];
        private readonly Lock _gate = new();

        public SymbolTape(DateTimeOffset since) => Since = since;

        public DateTimeOffset Since { get; }

        public (double Price, DateTimeOffset At)? Last { get; private set; }

        internal IReadOnlyList<TradeEvent> Observe(
            string exchangeSymbol, IReadOnlyList<AvTrade> trades, DateTimeOffset now)
        {
            var fresh = new List<TradeEvent>();
            lock (_gate)
            {
                foreach (var t in trades)
                {
                    if (t.Price is not { } price || price <= 0)
                    {
                        continue;
                    }

                    // A TRADE WITH NO CLOCK IS NOT A TRADE. Found on the host, not in review: the
                    // collector failed with `no partition of relation "trade" found for row` —
                    // a record whose timestamp was absent became the first of January 1970, and the
                    // trade table is partitioned by event time, so there is no partition that far
                    // back and never will be. It failed loudly, which is the good case; the bad one
                    // is a row in the earliest partition that exists, dated fifty-six years wrong.
                    //
                    // The bound is deliberately wide — anything this side of 2020 and before 2033 —
                    // because its job is to catch a MISSING field, not to second-guess the venue
                    // about when something happened.
                    if (t.Timestamp < 1_600_000_000 || t.Timestamp > 2_000_000_000)
                    {
                        continue;
                    }

                    var uid = $"{t.Hash}:{t.Id}";
                    if (!_seen.Add(uid))
                    {
                        continue;
                    }

                    var at = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp);
                    var notional = t.PositionSize ?? 0d;
                    var liquidation = t.IsLiquidation == true;
                    _rows.Add((uid, at, notional, price, liquidation));

                    // The last price has no window — a quiet market must not lose it just because
                    // its last print is old — so this stands before the age test below.
                    if (Last is not { } seen || at >= seen.At)
                    {
                        Last = (price, at);
                    }

                    // OLDER THAN THE WINDOW: remembered, not reported.
                    //
                    // Found on the host, and the epoch guard above did not catch it. "Recent trades"
                    // on a pair the venue has DELISTED can be months old — the adapter polls every
                    // pair in the catalogue, not only the listed ones — and `trade` is partitioned
                    // by event time with the current and next month in place. A print from August
                    // has no partition to land in, so the whole batch failed:
                    //
                    //   Npgsql 23514: no partition of relation "trade" found for row
                    //
                    // Cutting at the window is not a workaround for the partitioning: it is what
                    // this tape is FOR. Anything outside the rolling day is already outside every
                    // figure computed from it, so handing it on would persist a row no column reads.
                    if (at < now - Window)
                    {
                        continue;
                    }

                    fresh.Add(new TradeEvent(
                        ExchangeSymbol: exchangeSymbol,
                        EventTime: at,
                        VenueUid: uid,
                        Seq: null,
                        Price: price,
                        // The tape states a notional, not a quantity. Dividing it by the trade's own
                        // price is the venue's own arithmetic, not ours inventing a size — and the
                        // price used is the one that trade printed at, never a later one.
                        Qty: price > 0 ? notional / price : 0d,
                        // `buy` is the taker's direction on a venue where every fill is against the
                        // pool, so there is a taker and no maker.
                        TakerSide: t.Buy == true ? "buy" : "sell",
                        TradeType: liquidation ? "liquidation" : null));
                }

                Prune(now);
            }

            return fresh;
        }

        public double? Sum(DateTimeOffset now, bool liquidationsOnly)
        {
            lock (_gate)
            {
                Prune(now);
                if (_rows.Count == 0)
                {
                    // Seen nothing yet at all — distinct from a quiet day, which has rows that
                    // simply fell out of the window.
                    return Last is null ? null : 0d;
                }

                var floor = now - Window;
                var sum = 0d;
                foreach (var r in _rows)
                {
                    if (r.At >= floor && (!liquidationsOnly || r.Liquidation))
                    {
                        sum += r.Notional;
                    }
                }

                return sum;
            }
        }

        public IReadOnlyList<LiquidationBucket> Buckets(
            string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, int intervalSeconds, string unit)
        {
            if (intervalSeconds <= 0)
            {
                return [];
            }

            var byBucket = new Dictionary<long, double>();
            lock (_gate)
            {
                foreach (var r in _rows)
                {
                    if (!r.Liquidation || r.At < from || r.At > to)
                    {
                        continue;
                    }

                    var slot = r.At.ToUnixTimeSeconds() / intervalSeconds * intervalSeconds;
                    byBucket[slot] = byBucket.GetValueOrDefault(slot) + r.Notional;
                }
            }

            return byBucket
                .OrderBy(kv => kv.Key)
                .Select(kv => new LiquidationBucket(
                    exchangeSymbol,
                    intervalSeconds,
                    DateTimeOffset.FromUnixTimeSeconds(kv.Key),
                    kv.Value,
                    unit))
                .ToList();
        }

        /// <summary>Drops what has aged out of the window, rows and identities together — an
        /// identity set that only ever grows is a leak on a process that runs for weeks.</summary>
        private void Prune(DateTimeOffset now)
        {
            var floor = now - Window;
            if (_rows.Count == 0 || _rows[0].At >= floor)
            {
                return;
            }

            _rows.RemoveAll(r => r.At < floor);

            // The identity set is REBUILT from what survived, never merely cleared: a cleared set
            // would let a trade still inside the window be counted a second time on the next poll,
            // and the tape is re-read in full every minute. Rebuilt rather than pruned in step
            // because the set is small — about sixty entries a day on this venue's busiest pair.
            _seen.Clear();
            foreach (var r in _rows)
            {
                _seen.Add(r.Uid);
            }
        }
    }
}

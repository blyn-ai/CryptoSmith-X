using System.Collections.Concurrent;

namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// A per-symbol ring of recently seen 1-minute bars, built from a streaming candle channel rather
/// than a REST sweep. Two feeds share this — WEEX's <c>klineSnapshot</c>/<c>kline</c> and
/// Hyperliquid's <c>candle</c> — because the shape of "serve a range or refuse the whole thing" is
/// the same regardless of what protocol filled the buffer, and Binance's <c>kline</c> makes three.
///
/// <see cref="TryGetRange"/> refuses a PARTIAL answer on principle: a minute this cache has never
/// seen and a minute the venue genuinely traded nothing in look identical from here, so a hole
/// anywhere in the requested window fails the whole call rather than silently thinning it — the
/// caller falls back to REST for the entire range, the same ternary Kraken's ticker cache already
/// uses for the whole snapshot. This is also why a bar is never synthesised to fill a hole: a flat
/// candle we invented would be indistinguishable from one the venue actually reported.
///
/// Not every venue seeds history the same way. WEEX's <c>klineSnapshot</c> arrives with roughly five
/// hours of bars on every (re)subscribe; Hyperliquid's <c>candle</c> arrives with none at all — the
/// first frame after a subscribe is already just the live forming bar. Both are handled by the same
/// two calls: <see cref="Seed"/> for a bulk history push (call it with one bar for a venue that has
/// no bulk form), <see cref="Update"/> for a single live bar.
/// </summary>
public sealed class CandleCache
{
    /// <summary>Bound so a symbol nobody has resubscribed to for hours does not grow forever. WEEX's
    /// own <c>klineSnapshot</c> sends 301 bars (measured) — this is a margin over that observed
    /// depth, not a chosen retention window.</summary>
    private const int MaxBarsPerSymbol = 400;

    private readonly ConcurrentDictionary<string, SymbolBars> _bars = new(StringComparer.Ordinal);

    /// <summary>Replaces this symbol's whole buffer with a bulk history push (WEEX's
    /// <c>klineSnapshot</c>). Called on every (re)subscribe, so a reconnect that lost frames is
    /// corrected by the next snapshot rather than left holding a gap forever.</summary>
    public void Seed(string symbol, IReadOnlyList<Candle> history)
    {
        var bars = _bars.GetOrAdd(symbol, static _ => new SymbolBars());
        lock (bars.Gate)
        {
            bars.ByOpenTimeMs.Clear();
            foreach (var c in history)
            {
                bars.ByOpenTimeMs[c.OpenTime.ToUnixTimeMilliseconds()] = c;
            }

            Trim(bars);
        }
    }

    /// <summary>Upserts one live bar — the currently-forming minute, updated repeatedly until it
    /// rolls to the next one. No "is this closed" concept here: a still-forming bar is exactly what
    /// the REST path already tolerates writing (see <c>CandleCollector</c>'s comment on re-asking
    /// for the newest stored minute), and the next <see cref="Update"/> or <see cref="Seed"/>
    /// corrects it, the same way a later REST pass would.</summary>
    public void Update(string symbol, Candle bar)
    {
        var bars = _bars.GetOrAdd(symbol, static _ => new SymbolBars());
        lock (bars.Gate)
        {
            bars.ByOpenTimeMs[bar.OpenTime.ToUnixTimeMilliseconds()] = bar;
            Trim(bars);
        }
    }

    /// <summary>
    /// Every 1-minute bar in <c>[from, to)</c> — the same window <c>GetCandles1mAsync</c>'s REST
    /// paths already filter to (open time &gt;= from, open time + 1 minute &lt;= to) — if and only
    /// if this cache holds ALL of them. A missing minute anywhere fails the whole call; an empty but
    /// complete window (no full minute yet elapsed) returns true with an empty list, which is a
    /// legitimate answer REST would also give.
    /// </summary>
    public bool TryGetRange(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
    {
        candles = [];
        if (!_bars.TryGetValue(symbol, out var bars))
        {
            return false;
        }

        const long minuteMs = 60_000L;
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();
        var firstOpenMs = (fromMs + minuteMs - 1) / minuteMs * minuteMs;   // ceiling to the minute grid

        var list = new List<Candle>();
        lock (bars.Gate)
        {
            for (var t = firstOpenMs; t + minuteMs <= toMs; t += minuteMs)
            {
                if (!bars.ByOpenTimeMs.TryGetValue(t, out var bar))
                {
                    return false;   // a hole — the whole range is unusable, not just this minute
                }

                list.Add(bar);
            }
        }

        candles = list;
        return true;
    }

    public void Remove(string symbol) => _bars.TryRemove(symbol, out _);

    private static void Trim(SymbolBars bars)
    {
        var over = bars.ByOpenTimeMs.Count - MaxBarsPerSymbol;
        if (over <= 0)
        {
            return;
        }

        foreach (var k in bars.ByOpenTimeMs.Keys.Order().Take(over).ToList())
        {
            bars.ByOpenTimeMs.Remove(k);
        }
    }

    private sealed class SymbolBars
    {
        public readonly object Gate = new();
        public readonly Dictionary<long, Candle> ByOpenTimeMs = new();
    }
}

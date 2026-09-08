using System.Globalization;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// Open interest has no batched endpoint on Binance USDⓈ-M — <c>/fapi/v1/openInterest</c> answers
/// HTTP 400 <c>-1102</c> without a <c>symbol</c>, verified live, and no other public endpoint carries
/// it. That is exactly the case the 0001 schema anticipated ("OI is a separate, slower call on some
/// venues — hence its own time"), and it is the same shape as WEEX's, so this is the same solution:
/// cycle the known symbols continuously in the background, pace the calls, and serve whatever was
/// most recently learned from a <see cref="MarketCache{T}"/>. A symbol with no sample yet — or one
/// older than the freshness threshold — is simply absent, and the adapter omits its ticker rather
/// than writing a 0 nobody observed.
///
/// COST, stated plainly because it is the one unavoidable per-symbol REST cost this venue has left
/// once the book moves to the socket. Each call is weight 1 against a 2400/minute IP budget. The
/// in-scope set used to be ~570 trading perpetuals — the venue's whole listing — and a full pass
/// cost ~570 weight, a quarter of a minute's budget, which is why it was throttled to a 400 ms
/// trickle and took ~4 minutes to come back to a symbol. It now samples what we actually collect,
/// 44 symbols, so a pass costs 44 weight and the trickle is gone: the only tempo is the venue gate.
/// See <see cref="SymbolCycle"/> for the trade that makes — the pass repeats far more often than
/// <see cref="MaxAge"/> requires, and shares one budget with the collector loops.
/// </summary>
public sealed class BinanceOpenInterestFeed : IBinanceOpenInterestFeed
{
    private static readonly TimeSpan SymbolRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>How often a pass may START. A sample here is allowed to be <see cref="MaxAge"/> old,
    /// so once a minute is already fifteen times more often than the consumer needs; the pass itself
    /// takes about a second for the symbols we collect. See <see cref="SymbolCycle"/> for what
    /// running with no cadence at all cost when it was measured.</summary>
    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(1);

    /// <summary>How old a sample may be and still be served. Deliberately several times the cycle
    /// length: the threshold's job is to notice that the cycle has STOPPED, not to police the normal
    /// lag of a cycle that is running. A tighter number would omit every symbol the cycle happens to
    /// be walking away from, which is most of them, most of the time.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private readonly BinanceUsdmClient _client;
    private readonly VenueGate _gate;
    private readonly MarketCache<(double Oi, DateTimeOffset At)> _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private readonly Func<CancellationToken, Task<string[]>> _symbolsAsync;

    /// <param name="symbolsAsync">What to sample, from OUR database rather than the venue's listing:
    /// collected and trading. Binance lists 566 perpetuals and we store 44 of them, so the old
    /// venue-wide list spent 92 % of every pass on instruments nothing reads.</param>
    public BinanceOpenInterestFeed(
        BinanceUsdmClient client,
        VenueGate gate,
        Func<CancellationToken, Task<string[]>> symbolsAsync,
        ILoggerFactory loggers,
        TimeProvider clock)
    {
        _client = client;
        _gate = gate;
        _symbolsAsync = symbolsAsync;
        _clock = clock;
        _cache = new MarketCache<(double, DateTimeOffset)>(clock);
        _log = loggers.CreateLogger("Binance.OpenInterest");
    }

    public void Start(CancellationToken ct) => _ = RunAsync(ct);

    /// <summary>The most recent open interest for a symbol and when the VENUE sampled it — its own
    /// time, per the 0001 schema's <c>open_interest_at</c> — if a sample exists and is not older than
    /// the freshness threshold.</summary>
    public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
    {
        if (_cache.TryGet(symbol, MaxAge, out var entry))
        {
            openInterest = entry.Oi;
            at = entry.At;
            return true;
        }

        openInterest = 0;
        at = default;
        return false;
    }

    private Task RunAsync(CancellationToken ct) => SymbolCycle.RunAsync(
        "Binance.OpenInterest", _symbolsAsync, SymbolRefreshInterval, PassInterval, _gate,
        async (symbol, workCt) =>
        {
            // Through the venue ceiling, so these calls are counted against the same budget as
            // every other caller on this IP instead of running beside it unaccounted.
            BinanceOpenInterest oi;
            using (await _gate.AcquireAsync(workCt))
            {
                oi = await _client.GetOpenInterestAsync(symbol, workCt);
            }

            // The venue's own clock for this sample, not ours: open_interest_at exists precisely
            // because this number is measured on a different schedule from the snapshot it travels
            // with, and stamping it with our receive time would erase the distinction.
            var at = oi.Time > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(oi.Time)
                : _clock.GetUtcNow();

            _cache.Set(symbol, (double.Parse(oi.OpenInterest, CultureInfo.InvariantCulture), at));
        },
        _log, _clock, ct);
}

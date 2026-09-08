using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// Open interest has no batched endpoint on either of WEEX's API generations — it is a per-symbol
/// call, exactly the case the 0001 schema comment anticipated ("OI is a separate, slower call on some
/// venues — hence its own time"). Rather than one blocking call per symbol on every snapshot tick
/// (which would blow well past any sane request budget across ~1000 symbols), this cycles
/// through the known symbols continuously in the background, pacing calls, and serves whatever it has
/// most recently learned from a <see cref="MarketCache{T}"/>. A symbol with no sample yet — or one
/// older than the freshness threshold — is simply absent; the adapter treats that the same as it
/// treats a missing depth or size sample: the ticker for that symbol waits rather than lying with 0.
/// </summary>
public sealed class WeexOpenInterestFeed : IWeexOpenInterestFeed
{
    // No per-request pause any more: the 150 ms that used to live here made a pass over the venue's
    // ~990 contracts take ~7 minutes against a 10-minute freshness threshold — a margin of 1.4x, so a
    // pass a third slower would have started dropping open interest off the page and looking like a
    // venue outage. The list is now the 25 symbols we collect and the only tempo is the venue gate;
    // see SymbolCycle for what that trades away.
    private static readonly TimeSpan SymbolRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>How often a pass may START — ten times more often than <see cref="MaxAge"/> requires.
    /// This venue is the one that proved the point: with no cadence its 25 symbols came back sampled
    /// 34 s apart and the oldest reached 879 s against a 600 s threshold.</summary>
    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly WeexFuturesClient _client;
    private readonly VenueGate _gate;
    private readonly MarketCache<(double Oi, DateTimeOffset At)> _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private readonly Func<CancellationToken, Task<string[]>> _symbolsAsync;

    /// <param name="symbolsAsync">What to sample, from OUR database rather than the venue's listing:
    /// collected and trading. WEEX lists ~990 contracts and we store 25 of them.</param>
    public WeexOpenInterestFeed(
        WeexFuturesClient client,
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
        _log = loggers.CreateLogger("Weex.OpenInterest");
    }

    public void Start(CancellationToken ct) => _ = RunAsync(ct);

    /// <summary>The most recent open interest for a symbol and when it was sampled — its own time, per
    /// the 0001 schema's <c>open_interest_at</c> — if a sample exists and is not older than the
    /// freshness threshold.</summary>
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
        "Weex.OpenInterest", _symbolsAsync, SymbolRefreshInterval, PassInterval, _gate,
        async (symbol, workCt) =>
        {
            // Through the venue ceiling, so these calls are counted against the same budget as the
            // depth sweep instead of running beside it unaccounted.
            WeexOpenInterest oi;
            using (await _gate.AcquireAsync(workCt))
            {
                oi = await _client.GetOpenInterestAsync(symbol, workCt);
            }

            var value = double.Parse(oi.BaseVolume, System.Globalization.CultureInfo.InvariantCulture);
            _cache.Set(symbol, (value, _clock.GetUtcNow()));
        },
        _log, _clock, ct);
}

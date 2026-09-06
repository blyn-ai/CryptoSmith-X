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
    // This feed's own politeness, on top of the venue ceiling. The two are different things and both
    // are needed: the VenueGate is the hard ceiling shared with the depth sweep and every other
    // caller on this IP, while this trickle is how much of that shared budget open interest is
    // willing to take. ~6-7 req/s over ~1000 symbols is a full pass in ~2.5 min, well inside the
    // freshness threshold, and leaves the rest of the budget to the loops that need it.
    //
    // The 150 ms used to be justified by "WEEX's documented 20 req/s IP budget" — a number with no
    // vendor source behind it (see the 0021 migration header for where the claim actually came
    // from). 0021 has since found the real source and put WEEX's true budget on the exchange row
    // ('documented', ~200 req/s for weight-1 calls); this feed's own 150 ms is our additional
    // restraint on top of the shared VenueGate ceiling, not a restatement of a vendor number.
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SymbolRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly WeexFuturesClient _client;
    private readonly VenueGate _gate;
    private readonly MarketCache<(double Oi, DateTimeOffset At)> _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private volatile string[] _symbols = [];

    public WeexOpenInterestFeed(WeexFuturesClient client, VenueGate gate, ILoggerFactory loggers, TimeProvider clock)
    {
        _client = client;
        _gate = gate;
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

    private async Task RunAsync(CancellationToken ct)
    {
        var lastSymbolRefresh = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            if (_clock.GetUtcNow() - lastSymbolRefresh >= SymbolRefreshInterval)
            {
                await RefreshSymbolsAsync(ct);
                lastSymbolRefresh = _clock.GetUtcNow();
            }

            var symbols = _symbols;
            foreach (var symbol in symbols)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    // Through the venue ceiling, so these calls are counted against the same budget
                    // as the depth sweep instead of running beside it unaccounted.
                    WeexOpenInterest oi;
                    using (await _gate.AcquireAsync(ct))
                    {
                        oi = await _client.GetOpenInterestAsync(symbol, ct);
                    }

                    var value = double.Parse(oi.BaseVolume, System.Globalization.CultureInfo.InvariantCulture);
                    _cache.Set(symbol, (value, _clock.GetUtcNow()));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One symbol's failure must not stall the whole cycle; it just stays stale a
                    // little longer and the next pass retries it. A 429 is different in kind: it is
                    // the venue speaking about the whole IP, so it goes to the gate and slows every
                    // caller, not only this loop.
                    if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
                    {
                        _gate.Penalize();
                    }

                    _log.LogDebug(ex, "WEEX open interest fetch failed for {Symbol}", symbol);
                }

                try
                {
                    await Task.Delay(Pace, _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (symbols.Length == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        try
        {
            var contracts = await _client.GetContractsAsync(ct);
            _symbols = contracts.Select(c => c.Symbol).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "WEEX open interest feed: refreshing the symbol list failed; keeping the previous set");
        }
    }
}

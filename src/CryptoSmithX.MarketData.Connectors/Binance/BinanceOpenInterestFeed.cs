using System.Globalization;
using System.Net;
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
/// once the book moves to the socket. Each call is weight 1 against a 2400/minute IP budget, and the
/// in-scope set is ~570 trading perpetuals, so ONE FULL PASS COSTS ~570 WEIGHT — a quarter of a
/// minute's entire budget. At the <see cref="Pace"/> below a pass takes ~4 minutes and spends ~140
/// weight per minute, under 6 % of the budget, which is what makes it affordable at all. Asking for
/// open interest once per symbol per snapshot tick, the naive shape, would cost ~3400 weight per
/// minute at a 10 s snapshot interval: 140 % of everything Binance gives us, for one column.
/// </summary>
public sealed class BinanceOpenInterestFeed : IBinanceOpenInterestFeed
{
    /// <summary>This feed's own restraint, on top of the shared venue ceiling — the two are different
    /// things. <see cref="VenueGate"/> is the hard ceiling every caller on this IP contends for;
    /// this trickle is how much of that shared budget open interest is willing to take. 400 ms is
    /// 2.5 req/s: ~570 symbols in ~4 minutes, comfortably inside <see cref="MaxAge"/>, and it leaves
    /// the rest of the venue's budget to the loops that need it more.</summary>
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan SymbolRefreshInterval = TimeSpan.FromMinutes(10);

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

    private volatile string[] _symbols = [];

    public BinanceOpenInterestFeed(BinanceUsdmClient client, VenueGate gate, ILoggerFactory loggers, TimeProvider clock)
    {
        _client = client;
        _gate = gate;
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
                    // as every other caller on this IP instead of running beside it unaccounted.
                    BinanceOpenInterest oi;
                    using (await _gate.AcquireAsync(ct))
                    {
                        oi = await _client.GetOpenInterestAsync(symbol, ct);
                    }

                    // The venue's own clock for this sample, not ours: open_interest_at exists
                    // precisely because this number is measured on a different schedule from the
                    // snapshot it travels with, and stamping it with our receive time would erase
                    // the distinction the column was added for.
                    var at = oi.Time > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(oi.Time)
                        : _clock.GetUtcNow();

                    _cache.Set(symbol, (double.Parse(oi.OpenInterest, CultureInfo.InvariantCulture), at));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One symbol's failure must not stall the whole cycle; it just stays stale a
                    // little longer and the next pass retries it. A 429 is different in kind: it is
                    // the venue speaking about the whole IP, so it goes to the gate and slows every
                    // caller, not only this loop.
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                    {
                        _gate.Penalize();
                    }

                    _log.LogDebug(ex, "Binance open interest fetch failed for {Symbol}", symbol);
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

    /// <summary>
    /// The symbol set, from the venue's own listing and filtered by the SAME scope rule discovery
    /// applies. Only trading in-scope perpetuals: sampling open interest for a settling contract or
    /// an equity perpetual would spend the budget that the symbols we actually store are waiting for.
    /// </summary>
    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<BinanceSymbol> symbols;
            using (await _gate.AcquireAsync(ct))
            {
                symbols = await _client.GetSymbolsAsync(ct);
            }

            _symbols = symbols
                .Where(s => BinanceMarkets.IsInScope(s) && s.Status == "TRADING")
                .Select(s => s.Symbol)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Binance open interest feed: refreshing the symbol list failed; keeping the previous set");
        }
    }
}

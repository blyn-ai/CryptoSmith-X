using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Pacing;

/// <summary>
/// The background cycle three connector feeds had written out three times: refresh the symbol list
/// now and then, walk it sampling one thing per symbol, repeat forever. Each copy carried its own
/// hard-coded <c>Task.Delay</c> between requests — 400 ms on Binance, 150 on WEEX, 800 on
/// Hyperliquid — and those pauses, not the venue, were what made a pass take minutes: Binance's
/// open-interest cycle needed ~4 minutes to come back to a symbol, WEEX's ~7.
///
/// Two things changed and they are separate.
///
/// The per-REQUEST pauses are gone, and that is the change: they were what made a pass take minutes,
/// and a pass is now a bounded parallel sweep at the venue gate's own tempo.
///
/// A per-PASS cadence replaces them, and it is not the same mechanism wearing a different hat.
/// Measured on the test host when this ran with no cadence at all: the three feeds, looping with
/// nothing to slow them, kept the venue gate's queue permanently full, and since every claim is
/// placed behind the ones already queued, a feed worker finishing one symbol landed at the back for
/// the next. WEEX's 25 symbols came back sampled in strict alphabetical order roughly 34 SECONDS
/// apart — a serial walk far slower than the 150 ms pace that was removed — and the oldest sample
/// reached 879 s against a 600 s freshness threshold, so open interest started dropping off the page
/// exactly as the brief predicted it would if a pass grew. The collectors slowed with it: Kraken's
/// candle pass went from 33 s to 65 s.
///
/// So the tempo is now two numbers with two different jobs. WITHIN a pass, the venue gate alone
/// decides — no feed second-guesses the ceiling. BETWEEN passes, the feed states how often its
/// consumer actually needs a fresh sample, which for open interest is a small fraction of the
/// freshness threshold and nowhere near "constantly". Sampling something once a second that is
/// allowed to be ten minutes old is not diligence; it is spending a budget the collector loops are
/// queued for. Sharing that budget properly is still policy, and still belongs on the exchange row —
/// this is the honest floor under it, not a substitute for it.
///
/// The list narrowed. Each feed used to take its symbols from the VENUE's listing — all 566 Binance
/// perpetuals while 44 are collected, all ~990 WEEX contracts while 25 are — so most of every pass
/// was spent sampling instruments nobody stores. The list now comes from a delegate the Hub fills
/// from our own database, which is where the answer to "what do we collect" has always lived.
/// </summary>
public static class SymbolCycle
{
    /// <summary>Waiting this long before asking again when the symbol list comes back empty: a venue
    /// that lists nothing, or a database that has nothing switched on, must not spin.</summary>
    private static readonly TimeSpan EmptyBackoff = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs until <paramref name="ct"/> stops it. <paramref name="sampleAsync"/> is called once per
    /// symbol per pass and must acquire its own <see cref="VenueGate"/> lease around the request; a
    /// symbol that throws is logged and left stale, and a 429 penalises the venue for every caller.
    /// </summary>
    /// <param name="passInterval">Minimum time between the STARTS of two passes. A pass that takes
    /// longer than this simply starts the next one immediately; this is a floor on the cadence, not
    /// a delay added to it.</param>
    public static async Task RunAsync(
        string name,
        Func<CancellationToken, Task<string[]>> symbolsAsync,
        TimeSpan symbolRefreshInterval,
        TimeSpan passInterval,
        VenueGate gate,
        Func<string, CancellationToken, Task> sampleAsync,
        ILogger log,
        TimeProvider clock,
        CancellationToken ct)
    {
        var symbols = Array.Empty<string>();
        var lastRefresh = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            if (clock.GetUtcNow() - lastRefresh >= symbolRefreshInterval)
            {
                try
                {
                    symbols = await symbolsAsync(ct).ConfigureAwait(false);
                    lastRefresh = clock.GetUtcNow();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keeping the previous set is better than sampling nothing: the list changes
                    // rarely, and a database blip must not blank a feed the snapshot depends on.
                    log.LogWarning(ex, "{Feed}: refreshing the symbol list failed; keeping the previous set", name);
                }
            }

            if (symbols.Length == 0)
            {
                if (!await DelayAsync(EmptyBackoff, clock, ct).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            var startedTicks = clock.GetTimestamp();
            SweepResult pass;
            try
            {
                pass = await Sweep.RunAsync(
                    symbols,
                    gate.MaxConcurrentRequests,
                    async (symbol, workCt) =>
                    {
                        await sampleAsync(symbol, workCt).ConfigureAwait(false);
                        return 1;
                    },
                    ex =>
                    {
                        // A 429 is the venue speaking about the whole IP, so it goes to the shared
                        // gate and slows every caller, not only this feed.
                        VenuePenalty.Apply(gate, ex);
                        log.LogDebug(ex, "{Feed}: one symbol failed", name);
                    },
                    // One symbol's failure must not stall the cycle; it stays stale a little longer
                    // and the next pass retries it.
                    SweepFailure.Isolate,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var elapsed = clock.GetElapsedTime(startedTicks);

            // One line per pass, at Information: before this, how long a pass took was not knowable
            // from outside the process — the only evidence was a sample quietly ageing past its
            // freshness threshold, which reads as the venue's fault.
            log.LogInformation(
                "{Feed}: pass of {Symbols} symbols in {Seconds:F1} s, {Failed} failed",
                name, symbols.Length, elapsed.TotalSeconds, pass.Failed);

            var rest = passInterval - elapsed;
            if (rest > TimeSpan.Zero && !await DelayAsync(rest, clock, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>False when the wait was cut short by a stop.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, TimeProvider clock, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, clock, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

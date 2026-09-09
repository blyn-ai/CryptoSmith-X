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
/// The per-request pauses are gone. A feed that wants less of the venue's budget than the ceiling
/// allows was the right idea expressed as a constant nobody could see, tune, or reconcile with the
/// other callers; sharing one budget between feeds and loops is a policy question and belongs on the
/// exchange row next to the ceiling, not in three private fields. Until that exists the single tempo
/// WITHIN a pass is <see cref="VenueGate"/>, which is the thing the venue actually reacts to.
///
/// The list narrowed. Each feed used to take its symbols from the VENUE's listing — all 566 Binance
/// perpetuals while 44 are collected, all ~990 WEEX contracts while 25 are — so most of every pass
/// was spent sampling instruments nobody stores. The list now comes from a delegate the Hub fills
/// from our own database, which is where the answer to "what do we collect" has always lived.
///
/// A CADENCE BETWEEN PASSES, though, is back — see <paramref name="passInterval"/> below — and this
/// paragraph exists because it was tried once already (commit 1de2674) and withdrawn (0c26930) for a
/// reason that does not apply here: that attempt cited a measurement taken against code that had not
/// actually been committed, so what it "measured" was three feeds with NO cadence at all hammering a
/// venue gate whose queue they kept permanently full — evidence for the cadence's NECESSITY, not
/// against it, and withdrawn anyway because the commit could not be trusted to mean what it said. The
/// argument for a cadence is structural and does not need that number: a feed with no floor between
/// passes re-samples as fast as the gate allows, every one of its own workers landing back at the end
/// of the SAME queue the collector loops are standing in, and MaxAge on every one of these feeds is
/// already documented as "several times the cycle length" (see e.g.
/// <see cref="Weex.WeexOpenInterestFeed.MaxAge"/>) — a threshold sized for noticing the cycle has
/// stopped, not for policing the normal lag of one that is running once a second. Sampling something
/// once a second that is allowed to be minutes old is not diligence, it is spending a budget the
/// collector loops are queued behind.
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
    /// <param name="passInterval">The floor between the START of one pass and the START of the next
    /// — a pass that runs longer than this simply begins the next one immediately, so this bounds
    /// how OFTEN the feed asks, never how fast one pass itself may go (that is <see cref="VenueGate"/>
    /// alone, within the pass). Pick it from the consumer's own freshness need, not from how quickly
    /// the venue would tolerate being asked — see the class remarks for why "as fast as the gate
    /// allows" is the wrong default once one already existed.</param>
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

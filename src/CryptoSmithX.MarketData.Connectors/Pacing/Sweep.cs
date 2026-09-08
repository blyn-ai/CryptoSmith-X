using System.Runtime.ExceptionServices;

namespace CryptoSmithX.MarketData.Connectors.Pacing;

/// <summary>
/// What a sweep does when one item throws. The two collectors that walk a venue symbol by symbol
/// disagreed about this long before the sweep was shared, and both were right about their own case,
/// so the choice is a parameter rather than a decision baked into the loop.
/// </summary>
public enum SweepFailure
{
    /// <summary>Cancel the rest of the herd on the first failure and re-throw it once every worker
    /// has stopped. Depth's contract: a pass with a hole in it must arrive as a thrown exception,
    /// never as a smaller "successful" count, or the run is recorded ok and no gap is opened.</summary>
    FailFast,

    /// <summary>Count the failure, keep walking. Funding's and candles' contract: one venue symbol
    /// whose endpoint is broken — WEEX serves 400 for a live market's candles — must not starve
    /// every symbol behind it. Deciding that ALL of them failed is the caller's job, because the
    /// rule for it lives with the caller.</summary>
    Isolate
}

/// <summary>What a sweep produced. <see cref="Failed"/> and <see cref="LastError"/> are only
/// meaningful under <see cref="SweepFailure.Isolate"/> — a fail-fast sweep throws instead.</summary>
public readonly record struct SweepResult(int Written, int Failed, Exception? LastError);

/// <summary>
/// Walking a list of venue symbols with a bounded number of calls in flight. Extracted from
/// <c>DepthCollector</c>, which is where it was written and where it stayed while the two loops
/// beside it kept a plain <c>foreach</c> with an <c>await</c> in the middle — strictly one symbol at
/// a time, paying the venue's network latency once per symbol. On the test host that cost Kraken's
/// funding pass 74 s for 206 symbols (~360 ms each, never overlapping) against a measured round of
/// 32 parallel calls landing in well under 1.3 s on every venue we speak to.
///
/// This belongs beside <see cref="VenueGate"/> rather than in the Hub because the traversal and the
/// ceiling are one subject: how fast we are allowed to ask a venue for things. The gate says how
/// many may be in flight and how far apart they start; this says who walks the list. Putting it here
/// also lets the connectors' own background feeds use it, which is what stopped them needing a
/// hand-rolled pause of their own.
/// </summary>
public static class Sweep
{
    /// <summary>
    /// Runs <paramref name="workAsync"/> across <paramref name="items"/> with up to
    /// <paramref name="maxConcurrent"/> in flight, treating a failure according to
    /// <paramref name="onFailure"/>. <paramref name="onItemFailed"/> runs for every failure in both
    /// modes — it is how a 429 reaches the venue gate — and must not throw.
    ///
    /// Generic, and taking the failure hook as a delegate, so the contract can be pinned by a test
    /// that needs neither Postgres nor a venue.
    /// </summary>
    public static async Task<SweepResult> RunAsync<T>(
        IReadOnlyList<T> items,
        int maxConcurrent,
        Func<T, CancellationToken, Task<int>> workAsync,
        Action<Exception> onItemFailed,
        SweepFailure onFailure,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(workAsync);
        ArgumentNullException.ThrowIfNull(onItemFailed);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);

        if (items.Count == 0)
        {
            return new SweepResult(0, 0, null);
        }

        // Cancels the remaining work as soon as one item fails — under FailFast the pass is already
        // doomed, and continuing would spend venue budget on a result nobody will record. Created in
        // both modes so the worker body has one token to talk about; under Isolate nothing cancels it.
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var next = -1;
        var written = 0;
        var failed = 0;
        ExceptionDispatchInfo? failure = null;
        Exception? lastError = null;
        var failureLock = new object();

        async Task WorkAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= items.Count || failFast.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    Interlocked.Add(ref written, await workAsync(items[index], failFast.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // A stop is not a venue failure and must not be counted as one; it leaves this
                    // method through Task.WhenAll below, as it did when each collector had its own loop.
                    throw;
                }
                catch (Exception ex)
                {
                    lock (failureLock)
                    {
                        // The first failure wins under FailFast — it is the one that cancelled the
                        // others, so the siblings' cancellations cannot displace the real cause.
                        // LastError is the newest, matching what the per-symbol loops reported.
                        failure ??= ExceptionDispatchInfo.Capture(ex);
                        lastError = ex;
                        failed++;
                    }

                    onItemFailed(ex);

                    if (onFailure == SweepFailure.FailFast)
                    {
                        await failFast.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
        }

        var workers = new Task[Math.Min(maxConcurrent, items.Count)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = WorkAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (onFailure == SweepFailure.FailFast)
        {
            // Recorded, not swallowed: re-thrown here rather than folded into the returned count.
            failure?.Throw();
        }

        ct.ThrowIfCancellationRequested();

        return new SweepResult(written, failed, lastError);
    }
}

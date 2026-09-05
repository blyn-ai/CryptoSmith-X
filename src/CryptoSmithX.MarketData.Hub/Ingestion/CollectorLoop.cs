namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>What one iteration of a collector produced, as written to <c>collector_status</c>.</summary>
public sealed record CollectorAttempt(
    string ExchangeCode,
    string Collector,
    DateTimeOffset AttemptAt,
    bool Success,
    string? Error,
    int ConsecutiveFailures,
    int? InstrumentsExpected,
    long DurationMs,
    // How the data came in. REST and WS have different costs, different limits and different
    // failure modes, and until now they looked identical in the console. Every loop that writes
    // a run is REST today; the WS feeds do not report runs at all yet, which is itself something
    // the health page should eventually say out loud.
    string Transport = "rest");

/// <summary>
/// The only loop runner in the service. Every collector is this class with a different body, so
/// backoff, jitter, status writing and failure isolation exist once.
///
/// Status writing is a delegate rather than an interface because there is exactly one real
/// implementation; the tests pass a recording function instead of standing up Postgres.
/// </summary>
public sealed class CollectorLoop
{
    /// <summary>Backoff never grows past this multiple of the configured interval.</summary>
    public const int MaxBackoffFactor = 5;

    /// <summary>
    /// Consecutive failures after which the loop stops whispering. Three, to match the threshold
    /// the dashboard already calls "failing", so the console and the alert never disagree about
    /// what is broken.
    /// </summary>
    public const int FailuresBeforeAlert = 3;

    private readonly string _exchangeCode;
    private readonly string _collector;
    private readonly Func<TimeSpan> _interval;
    private readonly Func<CancellationToken, Task<int>> _body;
    private readonly Func<CollectorAttempt, CancellationToken, Task> _report;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;

    /// <param name="interval">
    /// Read on every iteration, not captured once, so an interval edited in the admin UI applies
    /// on the next cycle without a restart.
    /// </param>
    public CollectorLoop(
        string exchangeCode,
        string collector,
        Func<TimeSpan> interval,
        Func<CancellationToken, Task<int>> body,
        Func<CollectorAttempt, CancellationToken, Task> report,
        ILogger logger,
        TimeProvider clock)
    {
        _exchangeCode = exchangeCode;
        _collector = collector;
        _interval = interval;
        _body = body;
        _report = report;
        _logger = logger;
        _clock = clock;
    }

    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// Delay before the next attempt: the interval doubled per consecutive failure, capped at
    /// <see cref="MaxBackoffFactor"/>×. Pure, so the schedule can be asserted in a test.
    /// </summary>
    public static TimeSpan DelayFor(TimeSpan interval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return interval;
        }

        var factor = Math.Min(1 << Math.Min(consecutiveFailures, 30), MaxBackoffFactor);
        return interval * factor;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var attemptAt = _clock.GetUtcNow();
            CollectorAttempt attempt;

            var startedTicks = _clock.GetTimestamp();
            try
            {
                var count = await _body(ct).ConfigureAwait(false);
                ConsecutiveFailures = 0;
                attempt = new CollectorAttempt(
                    _exchangeCode, _collector, attemptAt, true, null, 0, count >= 0 ? count : null,
                    ElapsedMs(startedTicks));
                _logger.LogDebug(
                    "{Exchange}/{Collector} ok, {Count} rows", _exchangeCode, _collector, count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One venue failing must never stop another, so nothing escapes this loop.
                ConsecutiveFailures++;
                attempt = new CollectorAttempt(
                    _exchangeCode, _collector, attemptAt, false, Describe(ex), ConsecutiveFailures, null,
                    ElapsedMs(startedTicks));
                // Level by persistence, not by the exception. A venue rate-limiting one pass is
                // noise — Hyperliquid alone produced 68 of these in six hours and none of them
                // needed a person. A collector that has missed every attempt since the last two
                // is not having a bad minute, and it is the case Sentry exists for: the whole of
                // the last six hours logged not one Error, so nothing was sent while rollup sat
                // dead for four of them. Warning stays a breadcrumb, Error becomes an event.
                if (ConsecutiveFailures >= FailuresBeforeAlert)
                {
                    _logger.LogError(
                        ex, "{Exchange}/{Collector} failed ({Failures} in a row)",
                        _exchangeCode, _collector, ConsecutiveFailures);
                }
                else
                {
                    _logger.LogWarning(
                        ex, "{Exchange}/{Collector} failed ({Failures} in a row)",
                        _exchangeCode, _collector, ConsecutiveFailures);
                }
            }

            try
            {
                await _report(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Exchange}/{Collector} status write failed", _exchangeCode, _collector);
            }

            var delay = Jitter(DelayFor(_interval(), ConsecutiveFailures));
            try
            {
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private long ElapsedMs(long startedTicks) =>
        (long)_clock.GetElapsedTime(startedTicks).TotalMilliseconds;

    /// <summary>±10%, so a hundred instruments do not all wake in the same millisecond.</summary>
    private static TimeSpan Jitter(TimeSpan delay) =>
        delay * (0.9 + (Random.Shared.NextDouble() * 0.2));

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= 500 ? text : text[..500];
    }
}

using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Fake;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Hub.Retention;
using CryptoSmithX.MarketData.Hub.Rollups;
using CryptoSmithX.Database;
using Dapper;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// The supervisor. Rollup and retention run once for the whole service; the per-exchange collectors
/// are started and stopped to match each exchange's <c>status</c> in the database, reconciled every
/// ~30 s. Flipping an exchange to <c>enabled</c> in the admin UI starts its loops without a restart;
/// flipping it away cancels them. Configuration and intervals come from <see cref="DbSettings"/>,
/// read live, so the process holds no static options at all.
/// </summary>
public sealed class ExchangeWorker : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    // Rollup and retention are service-wide; their status rows are recorded against the fake
    // exchange, which is always present (seeded in 0002), so the collector_status FK holds.
    private const string ServiceExchange = "fake";

    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<ExchangeWorker> _logger;

    public ExchangeWorker(
        DbSettings settings,
        Db db,
        TimeProvider clock,
        ILoggerFactory loggers,
        ILogger<ExchangeWorker> logger)
    {
        _settings = settings;
        _db = db;
        _clock = clock;
        _loggers = loggers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Schema is verified in Program.cs before the host runs. Partitions stay with the Hub.
        await Partitions.EnsureCurrentAndNextAsync(_db, _clock, ct);
        await _settings.CurrentAsync(ct);   // prime the cache so interval providers can read it

        var rollup = new RollupJob(_settings, _db, _clock, _loggers.CreateLogger<RollupJob>());
        var retention = new RetentionJob(_settings, _db, _clock, _loggers.CreateLogger<RetentionJob>());

        var serviceLoops = new List<Task>
        {
            Loop(ServiceExchange, "rollup", () => TimeSpan.FromSeconds(60), rollup.RunAsync, ct),
            RunDailyAsync(retention, ct),
        };

        var running = new Dictionary<string, ExchangeRunner>(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReconcileAsync(running, ct);
                try
                {
                    await Task.Delay(ReconcileInterval, _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var runner in running.Values)
            {
                runner.Cts.Cancel();
            }

            await SafeWhenAll(running.Values.Select(r => r.Loops));
        }

        await SafeWhenAll(serviceLoops);
    }

    /// <summary>Start the loops of newly-enabled exchanges; stop those no longer enabled.</summary>
    private async Task ReconcileAsync(Dictionary<string, ExchangeRunner> running, CancellationToken ct)
    {
        var snapshot = await _settings.CurrentAsync(ct);
        var enabled = snapshot.Exchanges
            .Where(e => e.Status == "enabled")
            .ToDictionary(e => e.Code, StringComparer.Ordinal);

        foreach (var code in running.Keys.Where(c => !enabled.ContainsKey(c)).ToList())
        {
            _logger.LogInformation("Exchange {Exchange} is no longer enabled; stopping its collectors", code);
            running[code].Cts.Cancel();
            running.Remove(code);
        }

        foreach (var (code, config) in enabled)
        {
            if (running.ContainsKey(code))
            {
                continue;
            }

            IExchangeMarketData adapter;
            try
            {
                adapter = Build(config);
            }
            catch (Exception ex)
            {
                // A misconfigured enabled exchange must not take the supervisor down.
                _logger.LogWarning(ex, "Exchange {Exchange} is enabled but its adapter cannot be built; skipping", code);
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loops = await StartExchangeAsync(adapter, cts.Token);
            running[code] = new ExchangeRunner(cts, loops);
            _logger.LogInformation("Exchange {Exchange} is enabled; started its collectors", code);
        }
    }

    private async Task<Task> StartExchangeAsync(IExchangeMarketData adapter, CancellationToken ct)
    {
        var code = adapter.ExchangeCode;
        var discovery = new DiscoveryCollector(adapter, _settings, _db);
        var snapshot = new SnapshotCollector(adapter, _db, _clock);
        var depth = new DepthCollector(adapter, _db, _clock);
        var candles = new CandleCollector(adapter, _settings, _db, _clock);
        var funding = new FundingCollector(adapter, _settings, _db, _clock);

        // One discovery pass before the other loops so the first snapshot has rows to join to.
        try
        {
            var found = await discovery.RunAsync(ct);
            _logger.LogInformation("{Exchange}: initial discovery found {Count} instruments", code, found);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Exchange}: initial discovery failed", code);
        }

        var loops = new[]
        {
            Loop(code, "discovery", () => IntervalFor(code, (s, e) => s.DiscoveryInterval(e), TimeSpan.FromMinutes(60)), discovery.RunAsync, ct),
            Loop(code, "snapshot", () => IntervalFor(code, (s, e) => s.SnapshotInterval(e), TimeSpan.FromSeconds(10)), snapshot.RunAsync, ct),
            Loop(code, "depth", () => IntervalFor(code, (s, e) => s.DepthInterval(e), TimeSpan.FromSeconds(60)), depth.RunAsync, ct),
            Loop(code, "candles", () => IntervalFor(code, (s, e) => s.CandleInterval(e), TimeSpan.FromSeconds(60)), candles.RunAsync, ct),
            Loop(code, "funding", () => IntervalFor(code, (s, e) => s.FundingInterval(e), TimeSpan.FromMinutes(60)), funding.RunAsync, ct),
        };
        return Task.WhenAll(loops);
    }

    /// <summary>The effective interval for a collector, read live from the cached settings.</summary>
    private TimeSpan IntervalFor(string code, Func<SettingsSnapshot, ExchangeConfig, TimeSpan> pick, TimeSpan fallback)
    {
        var snapshot = _settings.Latest;
        var config = snapshot.Exchange(code);
        return config is null ? fallback : pick(snapshot, config);
    }

    private Task Loop(
        string exchangeCode, string collector, Func<TimeSpan> interval,
        Func<CancellationToken, Task<int>> body, CancellationToken ct)
    {
        var loop = new CollectorLoop(
            exchangeCode, collector, interval, body, WriteStatusAsync,
            _loggers.CreateLogger($"Collector.{exchangeCode}.{collector}"), _clock);
        return loop.RunAsync(ct);
    }

    private async Task RunDailyAsync(RetentionJob retention, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var dropped = await retention.RunAsync(ct);
                if (dropped > 0)
                {
                    _logger.LogInformation("Retention dropped {Count} snapshot partitions", dropped);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retention pass failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(24), _clock, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WriteStatusAsync(CollectorAttempt a, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into collector_status (
                exchange_code, collector, last_attempt_at, last_success_at, last_error_at,
                last_error, consecutive_failures, instruments_expected,
                last_duration_ms, avg_duration_ms)
            values (
                @ExchangeCode, @Collector, @AttemptAt,
                case when @Success then @AttemptAt end,
                case when @Success then null else @AttemptAt end,
                @Error, @ConsecutiveFailures, @InstrumentsExpected,
                @DurationMs, @DurationMs)
            on conflict (exchange_code, collector) do update set
                last_attempt_at      = excluded.last_attempt_at,
                last_success_at      = coalesce(excluded.last_success_at, collector_status.last_success_at),
                last_error_at        = coalesce(excluded.last_error_at,  collector_status.last_error_at),
                last_error           = case when @Success then collector_status.last_error else excluded.last_error end,
                consecutive_failures = excluded.consecutive_failures,
                instruments_expected = coalesce(excluded.instruments_expected, collector_status.instruments_expected),
                last_duration_ms     = excluded.last_duration_ms,
                -- EWMA: a cheap recent average with no history table; first sample seeds it whole
                avg_duration_ms      = coalesce(0.8 * collector_status.avg_duration_ms + 0.2 * excluded.last_duration_ms,
                                                excluded.last_duration_ms)
            """,
            new
            {
                a.ExchangeCode,
                a.Collector,
                a.AttemptAt,
                a.Success,
                a.Error,
                a.ConsecutiveFailures,
                a.InstrumentsExpected,
                a.DurationMs,
            },
            cancellationToken: ct));

        // The run history behind the UI's runs list and latency trend. Same connection,
        // separate statement — a failed insert here must not lose the status upsert above.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into collector_run (exchange_code, collector, started_at, duration_ms, ok, error, items)
            values (@ExchangeCode, @Collector, @AttemptAt, @DurationMs, @Success, @Error, @InstrumentsExpected)
            """,
            new { a.ExchangeCode, a.Collector, a.AttemptAt, a.DurationMs, a.Success, a.Error, a.InstrumentsExpected },
            cancellationToken: ct));
    }

    private static IExchangeMarketData Build(ExchangeConfig config) => config.Adapter switch
    {
        "fake" => new FakeExchangeMarketData(),
        "kraken-futures" => new KrakenFuturesMarketData(new KrakenFuturesClient(
            config.BaseUrl ?? throw new InvalidOperationException($"Exchange '{config.Code}' has no base_url"),
            config.ChartsUrl ?? throw new InvalidOperationException($"Exchange '{config.Code}' has no charts_url"))),
        _ => throw new InvalidOperationException(
            $"Exchange '{config.Code}' asks for adapter '{config.Adapter}', which does not exist yet. "
            + "Real adapters are added one per pull request."),
    };

    /// <summary>Await tasks, swallowing the cancellation that a normal stop raises.</summary>
    private static async Task SafeWhenAll(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private sealed record ExchangeRunner(CancellationTokenSource Cts, Task Loops);
}

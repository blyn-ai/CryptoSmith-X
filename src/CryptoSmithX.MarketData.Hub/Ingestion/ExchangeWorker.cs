using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Fake;
using CryptoSmithX.MarketData.Hub.Options;
using CryptoSmithX.MarketData.Hub.Retention;
using CryptoSmithX.MarketData.Hub.Rollups;
using CryptoSmithX.Database;
using Dapper;
using Microsoft.Extensions.Options;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Startup order and the set of running loops. Migrations and partitions come first, then one
/// discovery pass per exchange so snapshots always have an instrument to attach to, then the loops.
/// A failure here stops the service rather than leaving it half-started.
/// </summary>
public sealed class ExchangeWorker : BackgroundService
{
    private readonly MarketDataOptions _options;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<ExchangeWorker> _logger;

    public ExchangeWorker(
        IOptions<MarketDataOptions> options,
        Db db,
        TimeProvider clock,
        ILoggerFactory loggers,
        ILogger<ExchangeWorker> logger)
    {
        _options = options.Value;
        _db = db;
        _clock = clock;
        _loggers = loggers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Schema is verified in Program.cs before the host runs (a failure there exits non-zero).
        // Partitions stay with the Hub — it owns the writes.
        await Partitions.EnsureCurrentAndNextAsync(_db, _clock, ct);

        var adapters = new List<(IExchangeMarketData Adapter, ExchangeOptions Config)>();
        foreach (var cfg in _options.Exchanges.Where(e => e.Enabled))
        {
            if (!await IsEnabledInDbAsync(cfg.Code, ct))
            {
                _logger.LogInformation("Exchange {Exchange} is disabled in the database, skipping", cfg.Code);
                continue;
            }

            adapters.Add((Build(cfg), cfg));
        }

        if (adapters.Count == 0)
        {
            _logger.LogWarning("No exchange is enabled in both configuration and the database.");
        }

        var loops = new List<Task>();

        foreach (var (adapter, cfg) in adapters)
        {
            var discovery = new DiscoveryCollector(adapter, cfg, _options, _db);
            var snapshot = new SnapshotCollector(adapter, _db, _clock);
            var candles = new CandleCollector(adapter, _options, _db, _clock);

            // One pass before anything else runs, so the first snapshot has rows to join to.
            var found = await discovery.RunAsync(ct);
            _logger.LogInformation("{Exchange}: discovery found {Count} instruments", cfg.Code, found);

            loops.Add(Loop(cfg.Code, "discovery", _options.DiscoveryInterval, discovery.RunAsync, ct));
            loops.Add(Loop(cfg.Code, "snapshot", _options.SnapshotInterval, snapshot.RunAsync, ct));
            loops.Add(Loop(cfg.Code, "candles", _options.CandleInterval, candles.RunAsync, ct));
        }

        // One rollup and one retention for the whole service, recorded against the sentinel
        // exchange row so /health shows them next to the collectors.
        var rollup = new RollupJob(_options, _db, _clock, _loggers.CreateLogger<RollupJob>());
        var retention = new RetentionJob(_options, _db, _clock, _loggers.CreateLogger<RetentionJob>());

        var rollupExchange = adapters.Count > 0 ? adapters[0].Config.Code : "fake";
        loops.Add(Loop(rollupExchange, "rollup", TimeSpan.FromSeconds(60), rollup.RunAsync, ct));

        loops.Add(RunDailyAsync(retention, ct));

        await Task.WhenAll(loops);
    }

    private Task Loop(
        string exchangeCode, string collector, TimeSpan interval,
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

    private async Task<bool> IsEnabledInDbAsync(string code, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "select is_enabled from exchange where code = @code", new { code }, cancellationToken: ct)) ?? false;
    }

    private async Task WriteStatusAsync(CollectorAttempt a, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into collector_status (
                exchange_code, collector, last_attempt_at, last_success_at, last_error_at,
                last_error, consecutive_failures, instruments_expected)
            values (
                @ExchangeCode, @Collector, @AttemptAt,
                case when @Success then @AttemptAt end,
                case when @Success then null else @AttemptAt end,
                @Error, @ConsecutiveFailures, @InstrumentsExpected)
            on conflict (exchange_code, collector) do update set
                last_attempt_at      = excluded.last_attempt_at,
                last_success_at      = coalesce(excluded.last_success_at, collector_status.last_success_at),
                last_error_at        = coalesce(excluded.last_error_at,  collector_status.last_error_at),
                last_error           = case when @Success then collector_status.last_error else excluded.last_error end,
                consecutive_failures = excluded.consecutive_failures,
                instruments_expected = coalesce(excluded.instruments_expected, collector_status.instruments_expected)
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
            },
            cancellationToken: ct));
    }

    private static IExchangeMarketData Build(ExchangeOptions cfg) => cfg.Adapter.ToLowerInvariant() switch
    {
        "fake" => new FakeExchangeMarketData(),
        _ => throw new InvalidOperationException(
            $"Exchange '{cfg.Code}' asks for adapter '{cfg.Adapter}', which does not exist yet. "
            + "Real adapters are added one per pull request."),
    };
}

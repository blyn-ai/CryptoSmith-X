using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Fake;
using CryptoSmithX.MarketData.Connectors.Hyperliquid;
using CryptoSmithX.MarketData.Connectors.Kraken;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Connectors.Weex;
using CryptoSmithX.MarketData.Hub.Retention;
using CryptoSmithX.MarketData.Hub.Rollups;
using CryptoSmithX.Database;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// The supervisor. Rollup and retention run once for the whole service; the per-exchange collectors
/// are started and stopped to match each exchange's <c>status</c> in the database, reconciled every
/// ~30 s — and, since 0014, so is the SET of collector loops within an already-running exchange: which
/// ones run is data (<c>segment_dataset.mode='collect'</c> AND the adapter's declared capability),
/// not a fixed array. Toggling one dataset off cancels only that loop; the others are undisturbed.
/// Configuration comes from <see cref="DbSettings"/>, read live, so the process holds no static
/// options at all.
/// </summary>
public sealed class ExchangeWorker : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    // Rollup and retention are service-wide; their status rows are recorded against the fake
    // exchange, which is always present (seeded in 0002), so the collector_status FK holds.
    private const string ServiceExchange = "fake";

    /// <summary>The only datasets that have a real <c>Collector</c> class today. 'rollup' is the
    /// service-wide loop below, not a per-exchange one; trades/open_interest/liquidations have no
    /// implementation yet regardless of what policy says.</summary>
    internal static readonly string[] KnownCollectorDatasets = ["discovery", "snapshot", "depth", "candles", "funding"];

    private readonly DbSettings _settings;
    private readonly Db _db;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<ExchangeWorker> _logger;

    // One request ceiling per venue for the life of the process, shared by this exchange's
    // collectors and by the connectors' own background feeds. Held here rather than injected: the
    // Hub is the only process that talks to venues, and a second registry would be a second ceiling.
    private readonly VenueGates _gates;

    private readonly Dictionary<string, Func<CancellationToken, Task>> _serviceFactories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _serviceTasks = new(StringComparer.Ordinal);

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
        _gates = new VenueGates(clock);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Schema is verified in Program.cs before the host runs. Partitions stay with the Hub.
        await Partitions.EnsureCurrentAndNextAsync(_db, _clock, ct);
        await _settings.CurrentAsync(ct);   // prime the cache so interval providers can read it

        var rollup = new RollupJob(_settings, _db, _clock, _loggers.CreateLogger<RollupJob>(), ServiceExchange, "rollup");
        var retention = new RetentionJob(_settings, _db, _clock, _loggers.CreateLogger<RetentionJob>());

        // Service loops are held by factory so reconcile can restart one that died. They used to be
        // a bare list nobody awaited: when the rollup loop faulted, the exception went nowhere —
        // no log, no restart — and rollup silently stopped for three hours while every other
        // collector kept reporting green. A loop that is gone must never look like a loop that is idle.
        _serviceFactories["rollup"] = token =>
            Loop(ServiceExchange, "rollup", () => TimeSpan.FromSeconds(60), rollup.RunAsync, token);
        _serviceFactories["retention"] = token => RunDailyAsync(retention, token);

        foreach (var (name, factory) in _serviceFactories)
        {
            _serviceTasks[name] = factory(ct);
        }

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

            await SafeWhenAll(running.Values.SelectMany(r => r.Collectors.Values.Select(h => h.Task)));
        }

        await SafeWhenAll(_serviceTasks.Values);
    }

    /// <summary>
    /// A loop whose task has completed while its token is still live has crashed: restart it and say
    /// so loudly. Tracking a task without ever looking at it is the same as not tracking it — the
    /// dictionary key stays put, so the "is it missing?" check below can never notice.
    /// </summary>
    private void SuperviseServiceLoops(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        foreach (var (name, task) in _serviceTasks.ToList())
        {
            if (!task.IsCompleted)
            {
                continue;
            }

            _logger.LogError(task.Exception, "Service loop {Loop} stopped on its own; restarting", name);
            _serviceTasks[name] = _serviceFactories[name](ct);
        }
    }

    /// <summary>Start/stop exchanges to match <c>status</c>; for every exchange left running,
    /// reconcile which collector loops it should have.</summary>
    private async Task ReconcileAsync(Dictionary<string, ExchangeRunner> running, CancellationToken ct)
    {
        SuperviseServiceLoops(ct);

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
            if (!running.TryGetValue(code, out var runner))
            {
                // The per-exchange token is created first: a streaming adapter starts its socket from
                // Build and must die with this token when the exchange is disabled.
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                IExchangeMarketData adapter;
                VenueGate gate;
                try
                {
                    gate = GateFor(config, snapshot);
                    adapter = Build(config, gate, cts.Token);
                }
                catch (Exception ex)
                {
                    // A misconfigured enabled exchange must not take the supervisor down.
                    cts.Dispose();
                    _logger.LogWarning(ex, "Exchange {Exchange} is enabled but its adapter cannot be built; skipping", code);
                    continue;
                }

                try
                {
                    await WriteDeclaredCapabilityAsync(adapter, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "{Exchange}: writing declared capability failed", code);
                }

                runner = new ExchangeRunner(cts, adapter, BuildBodies(adapter, gate));
                running[code] = runner;

                // One discovery pass before the other loops so the first snapshot has rows to join to.
                try
                {
                    var found = await runner.Bodies["discovery"](cts.Token);
                    _logger.LogInformation("{Exchange}: initial discovery found {Count} instruments", code, found);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "{Exchange}: initial discovery failed", code);
                }

                _logger.LogInformation("Exchange {Exchange} is enabled; started", code);
            }

            ReconcileCollectors(runner, snapshot, code, ct);
        }
    }

    /// <summary>Which datasets a running exchange's loops should cover right now: policy says
    /// 'collect' AND the adapter actually implements it. Stops loops no longer wanted; starts loops
    /// newly wanted. Loops that stay wanted are left alone — their own interval keeps applying live,
    /// CollectorLoop already reads it fresh every iteration.</summary>
    private void ReconcileCollectors(ExchangeRunner runner, SettingsSnapshot snapshot, string code, CancellationToken parentCt)
    {
        var implemented = runner.Adapter.Capabilities.Select(c => c.DatasetCode);
        var desired = DesiredCollectors(snapshot, code, implemented).ToHashSet(StringComparer.Ordinal);

        foreach (var stale in runner.Collectors.Keys.Where(c => !desired.Contains(c)).ToList())
        {
            runner.Collectors[stale].Cts.Cancel();
            runner.Collectors.Remove(stale);
            _logger.LogInformation("{Exchange}/{Collector} no longer collected; loop stopped", code, stale);
        }

        // Same supervision for the per-exchange loops: a crashed one keeps its dictionary key, so
        // without this sweep it is indistinguishable from a healthy one and never comes back.
        foreach (var (name, handle) in runner.Collectors
                     .Where(kv => kv.Value.Task.IsCompleted && !kv.Value.Cts.IsCancellationRequested)
                     .ToList())
        {
            _logger.LogError(handle.Task.Exception, "{Exchange}/{Collector} loop stopped on its own; restarting", code, name);
            runner.Collectors.Remove(name);
        }

        foreach (var wanted in desired.Where(c => !runner.Collectors.ContainsKey(c)))
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(runner.Cts.Token);
            var datasetCode = wanted;
            var task = Loop(code, datasetCode, () => IntervalFor(code, datasetCode), runner.Bodies[wanted], cts.Token);
            runner.Collectors[wanted] = new CollectorHandle(cts, task);
            _logger.LogInformation("{Exchange}/{Collector} now collected; loop started", code, wanted);
        }
    }

    /// <summary>Desired loop set for one exchange: known-implementable datasets whose effective
    /// mode is 'collect' and whose adapter actually declares it. Pure, so the selection logic is
    /// testable without a supervisor or a database.</summary>
    internal static IReadOnlyList<string> DesiredCollectors(
        SettingsSnapshot snapshot, string segmentCode, IEnumerable<string> implementedDatasets)
    {
        var implemented = implementedDatasets.ToHashSet(StringComparer.Ordinal);
        return KnownCollectorDatasets
            .Where(c => implemented.Contains(c) && snapshot.Mode(segmentCode, c) == "collect")
            .ToList();
    }

    /// <summary>
    /// The venue's request ceiling for this segment — keyed on <c>exchange.code</c>, never on the
    /// segment code: two segments of one venue share one IP budget, which is the entire reason the
    /// venue level exists (0019). A segment whose venue row is missing is a misconfiguration the
    /// caller turns into "enabled but cannot be built", not a segment quietly running unpaced.
    /// </summary>
    private VenueGate GateFor(ExchangeConfig config, SettingsSnapshot snapshot)
    {
        var venue = snapshot.Venue(config.ExchangeCode)
            ?? throw new InvalidOperationException(
                $"Segment '{config.Code}' names venue '{config.ExchangeCode}', which has no exchange row — "
                + "no request budget can be resolved for it.");

        var gate = _gates.For(venue.Code, venue.RequestBudgetPerS, venue.MaxConcurrentRequests);
        if (gate.RequestsPerSecond != venue.RequestBudgetPerS || gate.MaxConcurrentRequests != venue.MaxConcurrentRequests)
        {
            // The gate was built earlier in this process from different numbers and keeps them; see
            // VenueGates for why. Say so, rather than letting the console show a budget nothing obeys.
            _logger.LogWarning(
                "Venue {Venue} budget in the database is {Rps} req/s x{Concurrency}, but the live gate runs "
                + "{LiveRps} req/s x{LiveConcurrency}; the change applies on restart",
                venue.Code, venue.RequestBudgetPerS, venue.MaxConcurrentRequests,
                gate.RequestsPerSecond, gate.MaxConcurrentRequests);
        }

        return gate;
    }

    /// <summary>One collector instance per known dataset, wired to this adapter — built once per
    /// exchange start so <see cref="ReconcileCollectors"/> only ever starts/stops the loops around
    /// them, never rebuilds them.</summary>
    private Dictionary<string, Func<CancellationToken, Task<int>>> BuildBodies(IExchangeMarketData adapter, VenueGate gate)
    {
        var discovery = new DiscoveryCollector(adapter, _settings, _db);
        var snapshot = new SnapshotCollector(adapter, _db, _settings, _clock, _loggers.CreateLogger<SnapshotCollector>());
        var depth = new DepthCollector(adapter, _db, gate);
        var candles = new CandleCollector(adapter, _settings, _db, _clock, gate);
        var funding = new FundingCollector(adapter, _settings, _db, _clock, gate);

        return new Dictionary<string, Func<CancellationToken, Task<int>>>(StringComparer.Ordinal)
        {
            ["discovery"] = discovery.RunAsync,
            ["snapshot"] = snapshot.RunAsync,
            ["depth"] = depth.RunAsync,
            ["candles"] = candles.RunAsync,
            ["funding"] = funding.RunAsync,
        };
    }

    /// <summary>The effective interval for a collector, read live from the cached settings.</summary>
    private TimeSpan IntervalFor(string code, string datasetCode) =>
        _settings.Latest.DatasetInterval(code, datasetCode);

    private Task Loop(
        string segmentCode, string collector, Func<TimeSpan> interval,
        Func<CancellationToken, Task<int>> body, CancellationToken ct)
    {
        var loop = new CollectorLoop(
            segmentCode, collector, interval, body, WriteStatusAsync,
            _loggers.CreateLogger($"Collector.{segmentCode}.{collector}"), _clock);
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
                segment_code, collector, last_attempt_at, last_success_at, last_error_at,
                last_error, consecutive_failures, instruments_expected,
                last_duration_ms, avg_duration_ms)
            values (
                @SegmentCode, @Collector, @AttemptAt,
                case when @Success then @AttemptAt end,
                case when @Success then null else @AttemptAt end,
                @Error, @ConsecutiveFailures, @InstrumentsExpected,
                @DurationMs, @DurationMs)
            on conflict (segment_code, collector) do update set
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
                a.SegmentCode,
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
            insert into collector_run (
                segment_code, collector, started_at, duration_ms, ok, error, items, transport)
            values (@SegmentCode, @Collector, @AttemptAt, @DurationMs, @Success, @Error,
                    @InstrumentsExpected, @Transport)
            """,
            new
            {
                a.SegmentCode, a.Collector, a.AttemptAt, a.DurationMs, a.Success, a.Error,
                a.InstrumentsExpected, a.Transport,
            },
            cancellationToken: ct));

        await RecordGapAsync(conn, a, ct);
    }

    /// <summary>
    /// Turns a run of failures into an explicit interval that was not observed.
    ///
    /// Absence is the default state of a database, which is exactly the problem: a row that is
    /// missing because the venue went quiet and a row that is missing because we were blind look
    /// identical, and a strategy trained on the second kind reads our outage as a market signal.
    /// A gap row makes the second kind a recorded fact.
    ///
    /// The first failure opens an interval, further failures leave it alone, and the next success
    /// closes it. Closing on success rather than on a timer is deliberate: only the collector
    /// getting data back proves the venue is answering again.
    /// </summary>
    private static async Task RecordGapAsync(NpgsqlConnection conn, CollectorAttempt a, CancellationToken ct)
    {
        if (a.Success)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                update collector_gap set gap_end = @AttemptAt
                 where segment_code = @SegmentCode and collector = @Collector and gap_end is null
                """,
                new { a.SegmentCode, a.Collector, a.AttemptAt },
                cancellationToken: ct));
            return;
        }

        // Only the first failure of a run opens the interval; the rest are the same outage.
        if (a.ConsecutiveFailures != 1)
        {
            return;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into collector_gap (segment_code, collector, gap_start, cause, detail)
            select @SegmentCode, @Collector, @AttemptAt, @Cause, @Detail
             where not exists (
                 select 1 from collector_gap
                  where segment_code = @SegmentCode and collector = @Collector and gap_end is null)
            """,
            new { a.SegmentCode, a.Collector, a.AttemptAt, Cause = CauseOf(a.Error), Detail = a.Error },
            cancellationToken: ct));
    }

    /// <summary>
    /// Classifies an outage from the error text the loop already captured. Coarse on purpose:
    /// the useful distinction is "the venue pushed us away" against "we broke", and a taxonomy
    /// finer than the evidence would invent precision that is not there.
    /// </summary>
    private static string CauseOf(string? error) => error switch
    {
        null => "error",
        var e when e.Contains("429", StringComparison.Ordinal)
                || e.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) => "rate_limited",
        var e when e.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                || e.Contains("timed out", StringComparison.OrdinalIgnoreCase) => "timeout",
        var e when e.Contains("maintenance", StringComparison.OrdinalIgnoreCase) => "exchange_maintenance",
        _ => "error",
    };

    /// <summary>Declares this adapter's capability into the matrix — 'we_implement' and
    /// 'transports_us' for every dataset, true/set where <see cref="IExchangeMarketData.Capabilities"/>
    /// lists it and false/empty otherwise. Runs once per exchange start (capability is a fixed fact
    /// about the adapter instance, not something that changes tick to tick). Never touches policy
    /// columns — only segment_dataset_capability.</summary>
    private async Task WriteDeclaredCapabilityAsync(IExchangeMarketData adapter, CancellationToken ct)
    {
        var implemented = adapter.Capabilities.ToDictionary(c => c.DatasetCode, StringComparer.Ordinal);

        await using var conn = await _db.OpenAsync(ct);
        // 'rollup' is excluded: it is hub-declared once by the 0014 migration seed, the same fact
        // for every exchange regardless of adapter (rollup has no real per-exchange axis — see the
        // migration header) — an adapter never mentions it in its own Capabilities, and this must
        // not read that silence as "we_implement=false" and clobber the seeded true.
        var datasets = await conn.QueryAsync<string>(new CommandDefinition(
            "select code from dataset where code <> 'rollup'", cancellationToken: ct));

        foreach (var datasetCode in datasets)
        {
            var weImplement = implemented.ContainsKey(datasetCode);
            var transportsUs = implemented.TryGetValue(datasetCode, out var cap) ? cap.TransportsUs : "";
            await DeclareAsync(conn, adapter.SegmentCode, datasetCode, "we_implement", weImplement ? "true" : "false", ct);
            await DeclareAsync(conn, adapter.SegmentCode, datasetCode, "transports_us", transportsUs, ct);
        }
    }

    /// <summary>Writes one declared capability value, logging to capability_log only when it actually
    /// changed from what was stored — so a routine restart, which declares the same facts again,
    /// leaves no noise.</summary>
    private static async Task DeclareAsync(
        System.Data.Common.DbConnection conn, string segmentCode, string datasetCode, string key, string newValue, CancellationToken ct)
    {
        var old = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select value from segment_dataset_capability where segment_code = @segmentCode and dataset_code = @datasetCode and capability_key = @key",
            new { segmentCode, datasetCode, key }, cancellationToken: ct));

        if (old == newValue)
        {
            return;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            update segment_dataset_capability
               set value = @newValue, source = 'declared', valid_since = now(), filled_at = now(), filled_by = 'reconcile'
             where segment_code = @segmentCode and dataset_code = @datasetCode and capability_key = @key
            """,
            new { segmentCode, datasetCode, key, newValue }, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into capability_log (segment_code, dataset_code, capability_key, old_value, new_value, source, changed_by)
            values (@segmentCode, @datasetCode, @key, @old, @newValue, 'declared', 'reconcile')
            """,
            new { segmentCode, datasetCode, key, old, newValue }, cancellationToken: ct));
    }

    private IExchangeMarketData Build(ExchangeConfig config, VenueGate gate, CancellationToken ct) => config.Adapter switch
    {
        "fake" => new FakeExchangeMarketData(),
        "kraken-futures" => BuildKraken(config, ct),
        "weex-futures" => BuildWeex(config, gate, ct),
        "hyperliquid" => BuildHyperliquid(config, gate, ct),
        _ => throw new InvalidOperationException(
            $"Exchange '{config.Code}' asks for adapter '{config.Adapter}', which does not exist yet. "
            + "Real adapters are added one per pull request."),
    };

    // Kraken's live market comes over WS when exchange.ws_url is set; the feed starts here and dies
    // with this exchange's token. Without a ws_url the adapter is pure REST, exactly as before. WS
    // honesty knobs are read live from settings at build time.
    private IExchangeMarketData BuildKraken(ExchangeConfig config, CancellationToken ct)
    {
        var baseUrl = config.BaseUrl ?? throw new InvalidOperationException($"Exchange '{config.Code}' has no base_url");
        var chartsUrl = config.ChartsUrl ?? throw new InvalidOperationException($"Exchange '{config.Code}' has no charts_url");
        var client = new KrakenFuturesClient(baseUrl, chartsUrl);

        KrakenWsFeed? ws = null;
        if (!string.IsNullOrWhiteSpace(config.WsUrl))
        {
            var settings = _settings.Latest;
            ws = new KrakenWsFeed(
                config.WsUrl, client, _loggers, _clock,
                settings.WsStaleAfter, settings.WsCrosscheckInterval, settings.WsCrosscheckDriftBps);
            ws.Start(ct);
        }

        return new KrakenFuturesMarketData(client, ws);
    }

    // WEEX is REST-only for now (see the commit that added this adapter for why WS was deferred).
    // Open interest has no batched endpoint on WEEX, so a background cycle keeps a fresh-enough
    // sample per symbol; it starts here and dies with this exchange's token, same as Kraken's WS feed.
    private IExchangeMarketData BuildWeex(ExchangeConfig config, VenueGate gate, CancellationToken ct)
    {
        var baseUrl = config.BaseUrl ?? throw new InvalidOperationException($"Exchange '{config.Code}' has no base_url");
        var client = new WeexFuturesClient(baseUrl);

        var openInterest = new WeexOpenInterestFeed(client, gate, _loggers, _clock);
        openInterest.Start(ct);

        return new WeexFuturesMarketData(client, openInterest);
    }

    // Hyperliquid always runs the REST book cycler (bid/ask/size and depth have no batched form on
    // this venue at all — see the commit that added this adapter), and additionally starts the WS
    // feed when ws_url is set, which the adapter prefers whenever it is healthy.
    private IExchangeMarketData BuildHyperliquid(ExchangeConfig config, VenueGate gate, CancellationToken ct)
    {
        var baseUrl = config.BaseUrl ?? throw new InvalidOperationException($"Exchange '{config.Code}' has no base_url");
        var client = new HyperliquidClient(baseUrl);

        var restFeed = new HyperliquidBookFeed(client, gate, _loggers, _clock);
        restFeed.Start(ct);

        HyperliquidWsFeed? ws = null;
        if (!string.IsNullOrWhiteSpace(config.WsUrl))
        {
            var settings = _settings.Latest;
            ws = new HyperliquidWsFeed(
                config.WsUrl, client, _loggers, _clock,
                settings.WsStaleAfter, settings.WsCrosscheckInterval, settings.WsCrosscheckDriftBps);
            ws.Start(ct);
        }

        return new HyperliquidMarketData(client, restFeed, ws);
    }

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

    private sealed record CollectorHandle(CancellationTokenSource Cts, Task Task);

    private sealed class ExchangeRunner
    {
        public ExchangeRunner(CancellationTokenSource cts, IExchangeMarketData adapter, Dictionary<string, Func<CancellationToken, Task<int>>> bodies)
        {
            Cts = cts;
            Adapter = adapter;
            Bodies = bodies;
        }

        public CancellationTokenSource Cts { get; }
        public IExchangeMarketData Adapter { get; }
        public Dictionary<string, Func<CancellationToken, Task<int>>> Bodies { get; }
        public Dictionary<string, CollectorHandle> Collectors { get; } = new(StringComparer.Ordinal);
    }
}

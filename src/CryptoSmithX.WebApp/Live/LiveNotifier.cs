using System.Text.Json;
using Npgsql;

namespace CryptoSmithX.WebApp.Live;

public delegate void LiveNotifyHandler(string exchangeCode, string? collector);

/// <summary>
/// Owns the one <c>LISTEN csx_live</c> connection for the whole process — not the pool <see cref="Db"/>
/// hands out, which cannot stay parked on a notification wait, so this opens its own dedicated,
/// unpooled connection. Every open SSE tab subscribes to <see cref="Notified"/> and filters for the
/// exchange it cares about; there is no per-tab database connection and no broker — "two users" does
/// not need one. A dropped connection (DB restart, network blip) reconnects with backoff; the SSE
/// side degrades to the 10 s poll on its own timeout, so a gap here is never silently invisible.
/// </summary>
public sealed class LiveNotifier : BackgroundService
{
    private const string Channel = "csx_live";
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly ILogger<LiveNotifier> _logger;

    public LiveNotifier(IConfiguration configuration, ILogger<LiveNotifier> logger)
    {
        var baseConnectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");
        // Pooling=false: a pooled connection can be handed back and reused mid-LISTEN by an unrelated
        // caller, silently dropping the subscription. This connection is parked on WaitAsync for the
        // life of the process and must never enter the shared pool.
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Pooling = false }.ToString();
        _logger = logger;
    }

    /// <summary>Fires on the worker's own thread for every notification once the payload parses
    /// cleanly. Exchange code and, for a per-collector event, the collector name; null collector means
    /// the exchange row or its whole policy matrix changed. Subscribers must not throw — an exception
    /// here would otherwise take the notifier's read loop down with it.</summary>
    public event LiveNotifyHandler? Notified;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                conn.Notification += OnNotification;
                await using (var cmd = new NpgsqlCommand($"LISTEN {Channel}", conn))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                _logger.LogInformation("LiveNotifier: listening on '{Channel}'", Channel);
                backoff = TimeSpan.FromSeconds(1);

                // WaitAsync blocks until a notification arrives (dispatching Notification synchronously
                // before returning) or the connection drops; looping here is the whole read pump.
                while (!ct.IsCancellationRequested)
                {
                    await conn.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LiveNotifier: connection lost, retrying in {Backoff}", backoff);
            }

            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.Payload);
            var exchange = doc.RootElement.GetProperty("exchange").GetString();
            var collector = doc.RootElement.TryGetProperty("collector", out var c) && c.ValueKind != JsonValueKind.Null
                ? c.GetString()
                : null;
            if (exchange is not null)
            {
                Notified?.Invoke(exchange, collector);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveNotifier: malformed payload '{Payload}'", args.Payload);
        }
    }
}

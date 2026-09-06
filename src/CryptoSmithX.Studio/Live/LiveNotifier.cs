using System.Text.Json;
using Npgsql;

namespace CryptoSmithX.Studio.Live;

/// <summary>
/// One thing that happened, as the stream sees it: a collector pass landed on a segment, or the
/// LISTEN connection itself changed state.
/// </summary>
/// <param name="Segment">
/// The segment the pass belongs to, or null when nothing happened in the market and this event is
/// only the connection reporting itself. Null is not "every segment" — it is "no segment", and a
/// subscriber that treated it as a wildcard would redraw the page every time the database blinked.
/// </param>
/// <param name="Collector">
/// The dataset code of the pass (<c>snapshot</c>, <c>depth</c>, <c>candles</c>…), or null when the
/// segment row or its whole policy matrix changed rather than one collector finishing.
/// </param>
/// <param name="Listening">Whether the LISTEN connection is up at the moment this event was made.</param>
public readonly record struct LiveEvent(string? Segment, string? Collector, bool Listening);

/// <summary>
/// The one <c>LISTEN csx_live</c> connection this process needs, opened only while somebody is
/// watching.
///
/// The admin console's <c>CryptoSmithX.WebApp.Admin.Live.LiveNotifier</c> is the same idea and it is a
/// <c>BackgroundService</c>: it holds its connection from start to shutdown, which is exactly right
/// for a console that two signed-in people keep open all day. This surface is the opposite shape —
/// anonymous, mostly unattended, and most of its visitors never press the button at all — so the
/// connection is reference counted: the first subscriber opens it, the last one to leave closes it.
/// A public page that holds a database connection all night to hear about a market nobody is
/// watching is paying a cost with no reader on the other end of it.
///
/// It is a copy rather than shared code because the original lives inside the WebApp.Admin application
/// project, and lifting it into <c>CryptoSmithX.Database</c> would rewire the admin console's live
/// plumbing from a step that is not about the admin console. The two are one extraction away and the
/// day a third surface wants this, that extraction is the right move; today it would be a change to
/// a working console made blind.
///
/// <b>LISTEN needs no grant.</b> Nothing in migration 0025 mentions it, and nothing has to:
/// LISTEN/NOTIFY is not table access, so <c>studio_reader</c> — a role that may select from eleven
/// tables and do nothing else — can subscribe to this channel. The public surface hears that a pass
/// finished; it still cannot read one byte it was not granted.
///
/// <b>Subscribers must not throw.</b> Handlers run on this class's own read loop, and an exception
/// escaping one would take the loop down with it — which would look, from the page, exactly like a
/// market that went quiet. They are called inside a catch for that reason.
/// </summary>
public sealed class LiveNotifier
{
    private const string Channel = "csx_live";
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly ILogger<LiveNotifier> _logger;
    private readonly Lock _gate = new();
    private readonly List<Action<LiveEvent>> _handlers = [];

    private CancellationTokenSource? _stop;
    private Task _pump = Task.CompletedTask;
    private volatile bool _listening;

    public LiveNotifier(IConfiguration configuration, ILogger<LiveNotifier> logger)
    {
        var baseConnectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        _connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            // Pooling off: a pooled connection can be handed back and reused mid-LISTEN by an
            // unrelated caller, silently dropping the subscription. This one parks on WaitAsync and
            // must never enter the shared pool — which also means it sits OUTSIDE the pool ceiling
            // of 20 in the connection string, as the 21st connection against the role's limit of 30
            // (0025). One connection, for the whole process, however many tabs are watching.
            Pooling = false,

            // A LISTEN connection is idle by definition, and an idle TCP connection that dies
            // between here and postgres does not announce itself: WaitAsync would sit on a socket
            // that will never speak again, this class would go on reporting that it is listening,
            // and every live page would show a market where nothing ever happens. That is the exact
            // failure this whole surface exists to make impossible, so the connection is made to
            // prove itself every 30 seconds instead of being trusted.
            KeepAlive = 30,
            TcpKeepAlive = true,
        }.ToString();

        _logger = logger;
    }

    /// <summary>Whether the channel is currently connected. The stream reports this to the browser
    /// in words: an open SSE connection whose source of events is down must not read as a quiet
    /// market.</summary>
    public bool Listening => _listening;

    /// <summary>How many subscribers are attached. Exposed for the health of the thing only.</summary>
    public int Subscribers
    {
        get
        {
            lock (_gate)
            {
                return _handlers.Count;
            }
        }
    }

    /// <summary>
    /// Attaches a handler and, if it is the first, opens the connection. Disposing detaches it and,
    /// if it was the last, closes the connection again.
    /// </summary>
    public IDisposable Subscribe(Action<LiveEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            _handlers.Add(handler);
            if (_handlers.Count == 1)
            {
                var cts = new CancellationTokenSource();
                _stop = cts;

                // Chained onto the previous pump rather than started beside it. A visitor who
                // toggles the button off and straight back on would otherwise have two loops
                // opening two connections and disagreeing about which one is listening; awaiting
                // the outgoing one first makes the pumps strictly serial.
                _pump = PumpAsync(_pump, cts.Token);
            }
        }

        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<LiveEvent> handler)
    {
        lock (_gate)
        {
            if (!_handlers.Remove(handler) || _handlers.Count > 0)
            {
                return;
            }

            // No lingering grace period. A reload with one viewer on the page closes and reopens the
            // connection a moment later, which is one extra connect against a local postgres; the
            // alternative is a timer holding a database connection open for a tab that is gone, and
            // the whole argument for reference counting was not wanting that.
            //
            // Cancelled and dropped, not disposed: the pump is still holding this token and will
            // unwind on it, and disposing a source while a Cancel is unwinding is the one way to
            // turn a tidy shutdown into an ObjectDisposedException. It holds no timer and no
            // registrations once the pump is gone, so letting the GC have it costs nothing.
            _stop?.Cancel();
            _stop = null;
        }
    }

    private async Task PumpAsync(Task previous, CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // The previous pump's failure is already logged where it happened, and it says nothing
            // about whether this one can connect.
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                conn.Notification += OnNotification;
                await using (var cmd = new NpgsqlCommand($"LISTEN {Channel}", conn))
                {
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                _logger.LogInformation("Studio live: listening on '{Channel}'", Channel);
                SetListening(true);
                backoff = TimeSpan.FromSeconds(1);

                while (!ct.IsCancellationRequested)
                {
                    // Blocks until a notification arrives — dispatching Notification synchronously
                    // before it returns — or the connection drops. This loop is the whole read pump.
                    await conn.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                SetListening(false);
                return;
            }
            catch (Exception ex)
            {
                SetListening(false);
                _logger.LogWarning(ex, "Studio live: connection lost, retrying in {Backoff}", backoff);
            }

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetListening(false);
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }

        SetListening(false);
    }

    private void SetListening(bool value)
    {
        if (_listening == value)
        {
            return;
        }

        _listening = value;

        // A state change is published like any other event, so a stream waiting on the next pass
        // wakes up and tells its reader that the source of passes has gone — rather than sitting
        // there looking healthy until the next heartbeat.
        Publish(new LiveEvent(null, null, value));
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        if (TryReadPass(args.Payload, out var segment, out var collector))
        {
            Publish(new LiveEvent(segment, collector, _listening));
        }
        else
        {
            _logger.LogWarning("Studio live: malformed payload '{Payload}'", args.Payload);
        }
    }

    /// <summary>
    /// The payload of one <c>csx_live</c> notification, as 0019 writes it:
    /// <c>{"segment": "...", "collector": "..." | null}</c>.
    /// </summary>
    /// <remarks>
    /// Public and static because it is the one piece of this class that can be tested without a
    /// database, and it is the piece that has to survive a surprise: a payload from a trigger this
    /// code has never seen must end as a false, not as an exception on the read loop. 0015 wrote the
    /// key as <c>exchange</c> and 0019 renamed it to <c>segment</c>; only the current shape is
    /// accepted, because a database old enough to still send the other one would fail
    /// <c>Migrator.VerifyAsync</c> before this class ever ran.
    /// </remarks>
    public static bool TryReadPass(string payload, out string? segment, out string? collector)
    {
        segment = null;
        collector = null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("segment", out var s)
                || s.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            segment = s.GetString();
            collector = doc.RootElement.TryGetProperty("collector", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            return segment is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Publish(LiveEvent e)
    {
        Action<LiveEvent>[] handlers;
        lock (_gate)
        {
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(e);
            }
            catch (Exception ex)
            {
                // One tab's handler failing must not silence every other tab. Swallowed here rather
                // than trusted to callers, because the caller is a request that may already be gone.
                _logger.LogWarning(ex, "Studio live: subscriber threw");
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly LiveNotifier _owner;
        private Action<LiveEvent>? _handler;

        public Subscription(LiveNotifier owner, Action<LiveEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        // Idempotent: a stream that leaves through both its finally and a disposal must not take the
        // reference count below the number of tabs actually watching.
        public void Dispose()
        {
            var handler = Interlocked.Exchange(ref _handler, null);
            if (handler is not null)
            {
                _owner.Unsubscribe(handler);
            }
        }
    }
}

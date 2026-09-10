using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoSmithX.WebApp.Studio.Live;

/// <summary>What this process's link to the hub is doing right now.</summary>
/// <remarks>
/// Three states and not two, for the same reason <see cref="LivePageController"/>'s signal has
/// three: a connection in the act of opening is not a connection that is down, and telling a reader
/// the feed is dead a fraction of a second before telling them it is fine is a page crying wolf on
/// its own start-up. <see cref="Down"/> means it broke and is being retried; nobody watching at all
/// is not a state here, because it is not a failure to report.
/// </remarks>
public enum HubStreamState
{
    Down,
    Opening,
    Up,
}

/// <summary>One instrument's live figures, as the hub sent them. Every field nullable, because null
/// is the venue's own answer — "this socket does not carry this" — and the page leaves that figure
/// on the database rather than showing a zero.</summary>
public sealed record HubQuote(
    int InstrumentId,
    string Segment,
    DateTimeOffset At,
    double? BidPrice,
    double? BidSize,
    double? AskPrice,
    double? AskSize,
    double? LastPrice,
    double? MarkPrice,
    double? IndexPrice,
    double? FundingRate,
    double? OpenInterest,
    double? Turnover24h);

/// <summary>One print off a venue's tape. No venue id and no sequence: a live view may miss prints,
/// and carrying an identity would invite the page to pretend it is the record.</summary>
public sealed record HubTrade(
    int InstrumentId,
    string Segment,
    DateTimeOffset At,
    double Price,
    double Qty,
    string TakerSide,
    string? TradeType);

public sealed record HubQuotesFrame(IReadOnlyList<HubQuote> Quotes);

public sealed record HubTradesFrame(IReadOnlyList<HubTrade> Trades);

/// <summary>
/// The one connection this process holds up to the hub, however many tabs are watching.
///
/// <b>The shape is <see cref="LiveNotifier"/>'s and that is deliberate.</b> That class holds one
/// <c>LISTEN</c> connection for the whole process, opened by the first subscriber and closed by the
/// last, because a public page that keeps a connection open for a market nobody is watching is
/// paying a cost with no reader on the other end. The same argument applies here, with one addition:
/// what the subscribers WANT changes. A tab opens on a second asset, another closes; the connection
/// has to follow that union without reopening once per arrival, which is what the debounce is for.
///
/// <b>Handlers must not throw.</b> They run on this class's own read loop, so an exception escaping
/// one would take the loop down — and from the page that looks exactly like a market that went
/// quiet. They are called inside a catch for that reason, again exactly as <see cref="LiveNotifier"/>
/// does it.
///
/// <b>Registered as a singleton and not as a hosted service</b>, for the reason the notifier's own
/// registration gives: there is nothing to start. It does nothing at all until a page subscribes,
/// and a hosted service would be a connection opened at boot for readers who may never arrive.
/// </summary>
public sealed class HubStream : IDisposable
{
    /// <summary>How long a burst of arrivals is allowed to keep changing the union before the
    /// connection is reopened against it. A page opening five listings must cost one reconnect, not
    /// five — and every reconnect is a gap for every OTHER asset already on this connection.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<HubStream> _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _firstBackoff;
    private readonly TimeSpan _maxBackoff;

    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _connection;
    private Task _pump = Task.CompletedTask;
    private Timer? _debounceTimer;
    private string _wantedNow = string.Empty;
    private volatile HubStreamState _state = HubStreamState.Opening;
    private volatile bool _disposed;

    public HubStream(HttpClient http, ILogger<HubStream> logger)
        : this(http, logger, DefaultDebounce, FirstBackoff, MaxBackoff)
    {
    }

    /// <summary>The timings are injectable for the tests and for nothing else — a debounce that can
    /// be raised from a config file is a debounce that gets raised instead of understood.</summary>
    internal HubStream(HttpClient http, ILogger<HubStream> logger, TimeSpan debounce, TimeSpan firstBackoff, TimeSpan maxBackoff)
    {
        _http = http;
        _logger = logger;
        _debounce = debounce;
        _firstBackoff = firstBackoff;
        _maxBackoff = maxBackoff;
    }

    public HubStreamState State => _state;

    public event Action<HubStreamState>? StateChanged;

    public event Action<HubQuotesFrame>? Quotes;

    public event Action<HubTradesFrame>? Trades;

    /// <summary>Adds these instruments to what this process asks the hub for. Dispose to withdraw
    /// them; the connection follows the union of everything still subscribed.</summary>
    public IDisposable Subscribe(IReadOnlyCollection<int> instrumentIds)
    {
        ArgumentNullException.ThrowIfNull(instrumentIds);

        var subscription = new Subscription(this, [.. instrumentIds]);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            if (_stop is null)
            {
                var cts = new CancellationTokenSource();
                _stop = cts;

                // Chained onto the previous pump rather than started beside it — LiveNotifier's own
                // reasoning: a reader who closes a tab and reopens it would otherwise have two loops
                // holding two connections and disagreeing about which one is up.
                _pump = PumpAsync(_pump, cts.Token);
            }

            ScheduleUnionCheck();
        }

        return subscription;
    }

    private void Withdraw(Subscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.Remove(subscription))
            {
                return;
            }

            ScheduleUnionCheck();
        }
    }

    /// <summary>Called under the lock on every arrival and departure. One timer, restarted — so a
    /// burst settles into a single reconsideration rather than one per event.</summary>
    private void ScheduleUnionCheck()
    {
        _debounceTimer ??= new Timer(_ => ApplyUnion(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void ApplyUnion()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var wanted = Union();
            if (wanted == _wantedNow)
            {
                // A second tab on an asset already being asked for wants nothing new. Reopening
                // would cost every other asset on this connection a gap for no new figure at all.
                return;
            }

            _wantedNow = wanted;

            // The reader is holding an open response for the previous union; cancelling it is how
            // the pump learns to go round again with the new one.
            _connection?.Cancel();
        }
    }

    private string Union()
    {
        var ids = new SortedSet<int>();
        foreach (var subscription in _subscriptions)
        {
            foreach (var id in subscription.InstrumentIds)
            {
                ids.Add(id);
            }
        }

        return string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    private async Task PumpAsync(Task previous, CancellationToken ct)
    {
        try
        {
            await previous;
        }
        catch
        {
            // The previous pump's own failure was already reported by the previous pump.
        }

        var backoff = _firstBackoff;
        while (!ct.IsCancellationRequested)
        {
            string wanted;
            CancellationTokenSource connection;
            lock (_gate)
            {
                wanted = _wantedNow;
                connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _connection = connection;
            }

            if (wanted.Length == 0)
            {
                // Nobody is watching. Not a failure and not reported as one — the state is left
                // where it is, so the next page to open is not told the hub is gone.
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, connection.Token);
                }
                catch (OperationCanceledException)
                {
                }

                connection.Dispose();
                continue;
            }

            var reopening = false;
            try
            {
                // Opening is announced only from a state that is not already Down: while a hub is out
                // and this loop is retrying, the page is told once, not once per attempt.
                if (_state != HubStreamState.Down)
                {
                    SetState(HubStreamState.Opening);
                }

                await ReadAsync(wanted, connection.Token);

                // The stream ended on its own — the hub restarted, or a proxy reaped it.
                SetState(HubStreamState.Down);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                connection.Dispose();
                break;
            }
            catch (OperationCanceledException)
            {
                // The union changed under us. Not a failure: go straight round without backoff and
                // without telling anyone the feed died.
                reopening = true;
            }
            catch (Exception ex)
            {
                SetState(HubStreamState.Down);
                _logger.LogWarning(ex, "Hub stream failed; retrying in {Backoff}", backoff);
            }
            finally
            {
                connection.Dispose();
            }

            if (reopening)
            {
                backoff = _firstBackoff;
                continue;
            }

            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = backoff >= _maxBackoff ? _maxBackoff : Doubled(backoff);
        }
    }

    private TimeSpan Doubled(TimeSpan backoff)
    {
        var next = backoff * 2;
        return next > _maxBackoff ? _maxBackoff : next;
    }

    private async Task ReadAsync(string instruments, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"/live?instruments={instruments}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);

        var name = string.Empty;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return;   // the hub closed the stream
            }

            // Up on the first line of ANY kind, including the ": connected" comment and the
            // heartbeat. Those are written by the hub's own handler, so they prove the endpoint is
            // answering — where response headers alone would only prove something accepted a socket.
            // Waiting for a QUOTE instead would leave a calm market reading as a dead feed, which is
            // the one confusion this whole signal exists to prevent.
            SetState(HubStreamState.Up);

            if (line.Length == 0)
            {
                name = string.Empty;
                continue;
            }

            if (line.StartsWith(':'))
            {
                // A comment — the heartbeat. Proof of life and nothing else.
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;   // id:, retry:, or something a later hub added and this one need not know
            }

            Dispatch(name, line[5..].Trim());
        }
    }

    private void Dispatch(string name, string payload)
    {
        try
        {
            switch (name)
            {
                case "quotes":
                    var quotes = JsonSerializer.Deserialize<WireQuote[]>(payload, Wire);
                    if (quotes is { Length: > 0 })
                    {
                        Raise(Quotes, new HubQuotesFrame([.. quotes.Select(q => q.ToQuote())]));
                    }

                    break;

                case "trades":
                    var trades = JsonSerializer.Deserialize<WireTrade[]>(payload, Wire);
                    if (trades is { Length: > 0 })
                    {
                        Raise(Trades, new HubTradesFrame([.. trades.Select(t => t.ToTrade())]));
                    }

                    break;
            }
        }
        catch (JsonException ex)
        {
            // A frame this process cannot read is one frame lost, not a reason to drop the
            // connection and every other asset riding on it.
            _logger.LogWarning(ex, "Hub stream sent a frame this build cannot read");
        }
    }

    private void SetState(HubStreamState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        Raise(StateChanged, state);
    }

    /// <summary>Every event this class raises goes through here. A subscriber that throws is logged
    /// and stepped over — the read loop belongs to every other subscriber too.</summary>
    private void Raise<T>(Action<T>? handlers, T payload)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A hub stream subscriber threw; the reader carries on");
            }
        }
    }

    /// <summary>Drives a frame into the subscribers without a connection — for tests of what a ROOM
    /// does with a frame, which is a separate question from how a frame arrives (that one is
    /// <c>HubStreamTests</c>, and it uses a real response stream). Internal, so nothing in the
    /// application can hand the page a figure no venue sent.</summary>
    internal void RaiseQuotes(HubQuotesFrame frame) => Raise(Quotes, frame);

    /// <summary>Sets the reported state without a connection, for the same reason and with the same
    /// restriction — a room's behaviour while the hub is down is worth a test, and holding a real
    /// socket open and killing it is not what that test is about.</summary>
    internal void ForceState(HubStreamState state) => SetState(state);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _subscriptions.Clear();
            _debounceTimer?.Dispose();
            _connection?.Cancel();
            _stop?.Cancel();
        }
    }

    private sealed class Subscription(HubStream stream, int[] instrumentIds) : IDisposable
    {
        private bool _withdrawn;

        public int[] InstrumentIds { get; } = instrumentIds;

        public void Dispose()
        {
            if (_withdrawn)
            {
                return;
            }

            _withdrawn = true;
            stream.Withdraw(this);
        }
    }

    private sealed record WireQuote(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("seg")] string Seg,
        [property: JsonPropertyName("at")] long At,
        [property: JsonPropertyName("bid")] double? Bid,
        [property: JsonPropertyName("bidSz")] double? BidSz,
        [property: JsonPropertyName("ask")] double? Ask,
        [property: JsonPropertyName("askSz")] double? AskSz,
        [property: JsonPropertyName("last")] double? Last,
        [property: JsonPropertyName("mark")] double? Mark,
        [property: JsonPropertyName("index")] double? Index,
        [property: JsonPropertyName("fund")] double? Fund,
        [property: JsonPropertyName("oi")] double? Oi,
        [property: JsonPropertyName("turn")] double? Turn)
    {
        public HubQuote ToQuote() => new(
            Id, Seg, DateTimeOffset.FromUnixTimeMilliseconds(At),
            Bid, BidSz, Ask, AskSz, Last, Mark, Index, Fund, Oi, Turn);
    }

    private sealed record WireTrade(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("seg")] string Seg,
        [property: JsonPropertyName("at")] long At,
        [property: JsonPropertyName("px")] double Px,
        [property: JsonPropertyName("qty")] double Qty,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("kind")] string? Kind)
    {
        public HubTrade ToTrade() => new(
            Id, Seg, DateTimeOffset.FromUnixTimeMilliseconds(At), Px, Qty, Side, Kind);
    }
}

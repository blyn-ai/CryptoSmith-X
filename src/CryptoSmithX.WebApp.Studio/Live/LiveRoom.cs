using System.Threading.Channels;
using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Live;

/// <summary>
/// One asset's live view, computed once per tick however many people are watching it.
///
/// <b>A room per ASSET, not per viewer.</b> The work of a tick is real — overlay every row, rank the
/// table again, format every cell — and it is identical for everyone looking at the same asset.
/// Fifty tabs on BTC is one computation here and fifty writes; per-viewer it would be fifty
/// computations, five times a second, all producing the same bytes.
///
/// <b>What it holds and why.</b> A baseline loaded exactly the way the first paint loads it, through
/// the same cache — so a stream and a reload of the tab beside it cannot disagree about the market.
/// The baseline is RELOADED on the collectors' own signal, because the live path carries quotes and
/// nothing else: open interest, depth, coverage and the gap list all move on a collector pass, and a
/// room that never reloaded would show those frozen under live prices.
///
/// <b>A viewer's channel holds one frame.</b> Capacity 1, drop oldest — that IS "skip a frame rather
/// than queue", and there is no other queue anywhere behind it. A reader on a slow connection sees
/// the newest state late, never a backlog of stale states in order.
/// </summary>
public sealed class LiveRoom : IDisposable
{
    private readonly PairPageLoad _load;
    private readonly HubStream _hub;
    private readonly LiveNotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _tick;

    private readonly Lock _gate = new();
    private readonly List<Channel<LiveFrame>> _viewers = [];
    private readonly Dictionary<int, HubQuote> _latest = [];
    private Dictionary<(int, string, int), LiveSlot> _sent = [];

    private IDisposable? _hubSubscription;
    private IDisposable? _notifierSubscription;
    private CancellationTokenSource? _stop;
    private Task _pump = Task.CompletedTask;

    private PairPageModel? _baseline;
    private volatile bool _baselineStale = true;
    private volatile bool _quotesArrived;
    private long _seq;
    private long _computations;
    private string _signal = "up";

    public LiveRoom(
        string baseFamily,
        PairPageLoad load,
        HubStream hub,
        LiveNotifier notifier,
        TimeProvider clock,
        ILogger logger,
        TimeSpan? tick = null)
    {
        BaseFamily = baseFamily;
        _load = load;
        _hub = hub;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
        _tick = tick ?? DefaultTick;
    }

    /// <summary>The hub's tick, matched exactly: a room that recomputed faster would be formatting
    /// values that cannot have changed, and one that recomputed slower would be holding frames the
    /// hub already sent.</summary>
    public static readonly TimeSpan DefaultTick = TimeSpan.FromMilliseconds(200);

    public string BaseFamily { get; }

    public int Viewers
    {
        get
        {
            lock (_gate)
            {
                return _viewers.Count;
            }
        }
    }

    /// <summary>How many ticks this room has actually computed. Logged once a minute, and the number
    /// that says whether "one computation per room" is still true — five tabs must cost what one
    /// tab costs.</summary>
    public long Computations => Interlocked.Read(ref _computations);

    /// <summary>Start watching. The first viewer starts the room; the reader is bounded to one frame
    /// on purpose (see the class remarks).</summary>
    public ChannelReader<LiveFrame> Join()
    {
        var channel = Channel.CreateBounded<LiveFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_gate)
        {
            _viewers.Add(channel);
            if (_viewers.Count == 1)
            {
                Start();
            }
        }

        return channel.Reader;
    }

    /// <summary>Stop watching. The last one out turns the room off — the timer, the hub subscription
    /// and the collectors' signal all go with it. A room left running for a tab that is gone is a
    /// computation five times a second for nobody.</summary>
    public void Leave(ChannelReader<LiveFrame> reader)
    {
        lock (_gate)
        {
            var channel = _viewers.FirstOrDefault(c => ReferenceEquals(c.Reader, reader));
            if (channel is null || !_viewers.Remove(channel))
            {
                return;
            }

            channel.Writer.TryComplete();
            if (_viewers.Count == 0)
            {
                Stop();
            }
        }
    }

    private void Start()
    {
        _baselineStale = true;
        _sent = [];

        // The collectors' own signal, exactly as the Latest mode uses it: a pass landed, so
        // everything the live path does NOT carry — depth, open interest, coverage, the gap list —
        // has moved and the baseline under these quotes is out of date.
        _notifierSubscription = _notifier.Subscribe(e =>
        {
            if (e.Segment is not null)
            {
                _baselineStale = true;
            }
        });

        _hubSubscription = _hub.Subscribe(InstrumentsOfBaseline());
        _hub.Quotes += OnQuotes;

        var cts = new CancellationTokenSource();
        _stop = cts;
        _pump = PumpAsync(cts.Token);
    }

    private void Stop()
    {
        _hub.Quotes -= OnQuotes;
        _hubSubscription?.Dispose();
        _hubSubscription = null;
        _notifierSubscription?.Dispose();
        _notifierSubscription = null;
        _stop?.Cancel();
        _stop = null;
        _latest.Clear();
    }

    private void OnQuotes(HubQuotesFrame frame)
    {
        lock (_gate)
        {
            foreach (var quote in frame.Quotes)
            {
                // Only what this room is about. One connection carries every asset being watched by
                // this process, so most of what arrives belongs to somebody else's room.
                if (_baseline is null || _baseline.Rows.Any(r => r.Row.InstrumentId == quote.InstrumentId))
                {
                    _latest[quote.InstrumentId] = quote;
                    _quotesArrived = true;
                }
            }
        }
    }

    private IReadOnlyCollection<int> InstrumentsOfBaseline() =>
        _baseline is null ? [] : [.. _baseline.Rows.Select(r => r.Row.InstrumentId)];

    private async Task PumpAsync(CancellationToken ct)
    {
        var lastReport = _clock.GetUtcNow();
        using var ticker = new PeriodicTimer(_tick, _clock);

        try
        {
            while (await ticker.WaitForNextTickAsync(ct))
            {
                await TickAsync(ct);

                if (_clock.GetUtcNow() - lastReport >= TimeSpan.FromMinutes(1))
                {
                    lastReport = _clock.GetUtcNow();
                    _logger.LogInformation(
                        "Live room {Asset}: {Computations} computations for {Viewers} viewers in the last minute",
                        BaseFamily, Interlocked.Exchange(ref _computations, 0), Viewers);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The last viewer left, or the process is going down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live room {Asset} stopped", BaseFamily);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        if (_baselineStale || _baseline is null)
        {
            _baselineStale = false;
            var reloaded = await _load(BaseFamily, ct);
            if (reloaded is not null)
            {
                var before = InstrumentsOfBaseline();
                _baseline = reloaded;

                // Discovery can list this asset on a venue that was not here when the room opened,
                // and the hub is only asked for what the page actually holds.
                if (!before.SequenceEqual(InstrumentsOfBaseline()))
                {
                    _hubSubscription?.Dispose();
                    _hubSubscription = _hub.Subscribe(InstrumentsOfBaseline());
                }
            }
        }

        if (_baseline is null)
        {
            return;
        }

        // The signal is settled BEFORE the "is there anything to say" test below, because a state
        // change is itself the news. With the hub gone no quotes arrive, so every later test would
        // pass and the reader would be told nothing at all — and a page that goes quiet when its
        // feed dies is indistinguishable from a page watching a quiet market. That is the one
        // failure this whole signal exists to prevent, and leaving it to the quotes reintroduced it.
        var signal = _hub.State == HubStreamState.Down ? "degraded" : "up";
        var signalMoved = signal != _signal;
        if (signalMoved && _signal == "degraded")
        {
            // Recovered. While the hub was gone this room was told nothing, so the reader's page is
            // as old as the outage: what they are owed first is the whole picture, not a diff
            // against one that has since moved.
            Resend();
        }

        _signal = signal;

        Dictionary<int, HubQuote> quotes;
        lock (_gate)
        {
            if (!_quotesArrived && _sent.Count > 0 && !signalMoved)
            {
                // Nothing arrived and the picture has already been sent once. The ages on screen
                // keep counting on their own — that is what studio-ages.js is for — so a frame
                // carrying nothing but a re-rendered age would be five writes a second for a number
                // the browser is already advancing.
                return;
            }

            _quotesArrived = false;
            quotes = new Dictionary<int, HubQuote>(_latest);
        }

        Interlocked.Increment(ref _computations);

        var now = _clock.GetUtcNow();
        var rows = LiveFrames.Overlay(_baseline.Rows, quotes, now);
        var slots = LiveFrames.Slots(rows, Verdicts.Compute(rows), quotes, now);

        var changed = LiveFrames.Diff(_sent, slots);
        if (changed.Count == 0 && !signalMoved)
        {
            return;
        }

        // A frame carrying no slots at all is not empty: when the signal moved and no figure did,
        // the signal IS the frame. Said out loud rather than left for the reader to infer from
        // figures that stopped moving.

        _sent = slots.ToDictionary(s => s.Key);
        var frame = new LiveFrame(Interlocked.Increment(ref _seq), changed, signal);

        lock (_gate)
        {
            foreach (var viewer in _viewers)
            {
                // Never awaited and never blocking: the channel drops its previous frame if this
                // reader has not taken it. One slow viewer must not hold up the room.
                viewer.Writer.TryWrite(frame);
            }
        }
    }

    /// <summary>Resends the whole picture on the next tick — what a reader owes a reconnect, and
    /// what a recovered hub owes everyone.</summary>
    public void Resend()
    {
        lock (_gate)
        {
            _sent = [];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var viewer in _viewers)
            {
                viewer.Writer.TryComplete();
            }

            _viewers.Clear();
            Stop();
        }
    }
}

/// <summary>How a room loads its baseline — the page's own loader, passed in rather than reached
/// for, so a room can be driven in a test without a database.</summary>
public delegate Task<PairPageModel?> PairPageLoad(string baseFamily, CancellationToken ct);

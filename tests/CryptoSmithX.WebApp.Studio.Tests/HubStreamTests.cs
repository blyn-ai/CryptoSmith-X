using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CryptoSmithX.WebApp.Studio.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The one connection this process holds up to the hub, and the promises around it.
///
/// The shape is <see cref="LiveNotifier"/>'s, deliberately and for the same reason: however many
/// tabs are watching, upstream there is ONE subscription, opened by the first watcher and closed by
/// the last. What is different is that the union of what those tabs want changes — a second asset
/// opens, a first one closes — and the connection has to follow it without thrashing, which is what
/// the debounce is for.
///
/// Proven against a fake handler serving an event stream from a string: no hub, no socket, no
/// network. The debounce and backoff are injected short so the tests take milliseconds; production
/// values are the class's own defaults and are asserted separately.
/// </summary>
public sealed class HubStreamTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task Two_subscriptions_open_one_connection()
    {
        var handler = new StreamHandler();
        using var hub = Stream(handler);

        using var a = hub.Subscribe([1, 2]);
        using var b = hub.Subscribe([2, 3]);

        await handler.WaitForRequests(1);
        await Task.Delay(Debounce * 6);

        // One connection, carrying the union — not one per watcher, and not one per asset.
        Assert.Equal(1, handler.Requests);
        Assert.Equal("1,2,3", handler.LastInstruments);
    }

    [Fact]
    public async Task Union_changes_reconnect_once_after_debounce()
    {
        var handler = new StreamHandler();
        using var hub = Stream(handler);

        using var a = hub.Subscribe([1]);
        await handler.WaitForRequests(1);

        // Three arrivals inside one debounce window are one reconnect, not three: a page opening
        // five listings must not open five connections on the way to opening one.
        var b = hub.Subscribe([2]);
        var c = hub.Subscribe([3]);
        var d = hub.Subscribe([4]);

        await handler.WaitForRequests(2);
        await Task.Delay(Debounce * 6);

        Assert.Equal(2, handler.Requests);
        Assert.Equal("1,2,3,4", handler.LastInstruments);

        b.Dispose();
        c.Dispose();
        d.Dispose();
    }

    [Fact]
    public async Task A_subscription_that_adds_nothing_new_does_not_reconnect()
    {
        var handler = new StreamHandler();
        using var hub = Stream(handler);

        using var a = hub.Subscribe([1, 2]);
        await handler.WaitForRequests(1);
        await Task.Delay(Debounce * 4);

        // A second tab on the same asset wants exactly what is already being asked for. Reopening
        // would cost every other tab a gap for no new figure at all.
        using var b = hub.Subscribe([1, 2]);
        await Task.Delay(Debounce * 6);

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Last_subscriber_gone_closes_the_connection()
    {
        var handler = new StreamHandler();
        using var hub = Stream(handler);

        var a = hub.Subscribe([1]);
        await handler.WaitForRequests(1);
        Assert.True(handler.Open);

        a.Dispose();
        await Task.Delay(Debounce * 10);

        // Nobody is watching, so nothing is held open — and that is NOT a failure: the state must
        // not read Down, or the next page to open would be told the hub is gone.
        Assert.False(handler.Open);
        Assert.NotEqual(HubStreamState.Down, hub.State);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_reader()
    {
        // LiveNotifier's rule, verbatim: handlers run on this class's own read loop, so one that
        // throws would take the loop down with it — and from the page that looks exactly like a
        // market that went quiet.
        var handler = new StreamHandler();
        handler.Enqueue(Quotes(1, 100), Quotes(1, 101), Quotes(1, 102));
        using var hub = Stream(handler);

        var seen = 0;
        hub.Quotes += _ => throw new InvalidOperationException("subscriber is broken");
        hub.Quotes += _ => Interlocked.Increment(ref seen);

        using var a = hub.Subscribe([1]);
        await WaitUntil(() => Volatile.Read(ref seen) >= 3);

        Assert.True(Volatile.Read(ref seen) >= 3);
    }

    [Fact]
    public async Task A_drop_is_reported_once_and_recovery_once()
    {
        // While retrying, the state stays Down rather than flickering Down/Opening on every attempt:
        // a page would otherwise be told the feed died once a second for as long as the hub is out.
        var handler = new StreamHandler { FailUntilRequest = 4 };
        using var hub = Stream(handler);

        var states = new ConcurrentQueue<HubStreamState>();
        hub.StateChanged += s => states.Enqueue(s);

        using var a = hub.Subscribe([1]);
        await WaitUntil(() => states.Contains(HubStreamState.Up), TimeSpan.FromSeconds(5));

        var reported = states.ToArray();
        Assert.Equal(1, reported.Count(s => s == HubStreamState.Down));
        Assert.Equal(1, reported.Count(s => s == HubStreamState.Up));
        Assert.Equal(HubStreamState.Up, hub.State);
    }

    [Fact]
    public async Task Quotes_arrive_parsed_with_the_feeds_own_instant()
    {
        var handler = new StreamHandler();
        handler.Enqueue("""[{"id":7,"seg":"kraken-futures","at":1789070702149,"bid":77128.5,"mark":77130}]""");
        using var hub = Stream(handler);

        HubQuotesFrame? got = null;
        hub.Quotes += f => got = f;

        using var a = hub.Subscribe([7]);
        await WaitUntil(() => got is not null);

        var quote = Assert.Single(got!.Quotes);
        Assert.Equal(7, quote.InstrumentId);
        Assert.Equal(77128.5, quote.BidPrice);
        Assert.Equal(77130, quote.MarkPrice);
        Assert.Null(quote.AskPrice);   // absent on the wire means the venue has none, not zero
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789070702149), quote.At);
    }

    [Fact]
    public async Task A_malformed_frame_is_skipped_and_the_reader_carries_on()
    {
        var handler = new StreamHandler();
        handler.EnqueueRaw("event: quotes\ndata: {not json\n\n");
        handler.Enqueue(Quotes(1, 100));
        using var hub = Stream(handler);

        var seen = 0;
        hub.Quotes += _ => Interlocked.Increment(ref seen);

        using var a = hub.Subscribe([1]);
        await WaitUntil(() => Volatile.Read(ref seen) >= 1);

        Assert.Equal(1, Volatile.Read(ref seen));
    }

    [Fact]
    public void The_production_debounce_and_backoff_are_what_the_plan_says() =>
        // Injected short in the tests above; these are the numbers that actually ship.
        Assert.Equal(
            (TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)),
            (HubStream.DefaultDebounce, HubStream.FirstBackoff, HubStream.MaxBackoff));

    // ---------------------------------------------------------------------------------------

    private static HubStream Stream(StreamHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://hub.test:8080") },
            NullLogger<HubStream>.Instance, Debounce, Backoff, Backoff);

    private static string Quotes(int id, double bid) =>
        $$"""[{"id":{{id}},"seg":"kraken-futures","at":1789070702149,"bid":{{bid}}}]""";

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(5);
        }
    }

    /// <summary>Serves an event stream from strings, counts connections, and can be told to fail the
    /// first N of them — the three things every test here needs and none of which needs a socket.</summary>
    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _frames = new();
        private int _requests;

        /// <summary>Requests below this number fail outright, so a drop and a recovery can both be
        /// observed on one stream.</summary>
        public int FailUntilRequest { get; init; }

        public int Requests => Volatile.Read(ref _requests);

        public bool Open { get; private set; }

        public string? LastInstruments { get; private set; }

        public void Enqueue(params string[] payloads)
        {
            foreach (var p in payloads)
            {
                _frames.Enqueue($"event: quotes\ndata: {p}\n\n");
            }
        }

        public void EnqueueRaw(string frame) => _frames.Enqueue(frame);

        public async Task WaitForRequests(int count)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < deadline && Requests < count)
            {
                await Task.Delay(5);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _requests);
            LastInstruments = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["instruments"];

            if (n < FailUntilRequest)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var body = new StringBuilder(": connected\n\n");
            while (_frames.TryDequeue(out var frame))
            {
                body.Append(frame);
            }

            Open = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HeldStream(Encoding.UTF8.GetBytes(body.ToString()), () => Open = false)),
            });
        }
    }

    /// <summary>A body that serves its bytes and then BLOCKS rather than ending, so "the connection
    /// is still open" is a state a test can observe. Closing it is what says the reader let go.</summary>
    private sealed class HeldStream(byte[] bytes, Action onClose) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < bytes.Length)
            {
                var n = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsSpan(_position, n).CopyTo(buffer.Span);
                _position += n;
                return n;
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            onClose();
            base.Dispose(disposing);
        }
    }
}

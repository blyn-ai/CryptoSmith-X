using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Binance;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The three things that turned one bad afternoon into a week: a socket torn down by our own handler
/// and reported as the venue dropping us, a message decoded a fragment at a time, and a liveness alarm
/// that read the wrong connection's counter and then named the wrong setting.
///
/// None of these is about throughput. The theory under investigation was that the hub could not drain
/// 566 depth streams fast enough and Binance was closing a consumer that had stopped reading; measured,
/// the drain path runs at 15-29x the live frame rate and its queueing lag is flat, so a bounded channel
/// between the socket and the parser would have been a rewrite of the venue's depth feed in service of
/// a refuted hypothesis. What the investigation actually turned up were these three, and they are what
/// is pinned here. The drop policy that rewrite would have needed a decision about is settled for us by
/// the venue: a lost diff is caught by the next frame's <c>pu</c>, which
/// <see cref="BinanceWsTests.A_missed_frame_stops_the_book_rather_than_corrupting_it"/> already proves
/// leaves the book dirty and serving nothing rather than clean and wrong.
/// </summary>
public sealed class WsResilienceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 19, 34, 0, TimeSpan.Zero);

    // ── The receive loop keeps draining while the handler misbehaves ──────
    [Fact]
    public async Task A_handler_that_throws_does_not_cost_the_connection()
    {
        // The exact production shape: the venue keeps sending, our handler chokes on some of it. Before
        // the guard, frame 2 ended the receive loop, RunAsync logged "WS connection dropped" against the
        // venue, and the reconnect delivered frame 1 again — 11 frames, 11 reconnects in 12 s on the
        // bench. What must be true now is that all five frames reach the handler on ONE connection.
        using var server = new LoopbackWsServer(async (ws, ct) =>
        {
            for (var i = 1; i <= 5; i++)
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(i.ToString()), WebSocketMessageType.Text, true, ct);
            }

            await Task.Delay(Timeout.Infinite, ct);
        });

        var seen = new List<string>();
        var allFive = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var conn = new WsConnection(server.Url, NullLogger.Instance, TimeProvider.System);
        var run = conn.RunAsync(
            _ => Task.CompletedTask,
            text =>
            {
                lock (seen)
                {
                    seen.Add(text);
                    if (seen.Count == 5)
                    {
                        allFive.TrySetResult();
                    }
                }

                // Stands in for HandleDepth's Deserialize on a frame the DTOs cannot bind.
                if (text is "2" or "3")
                {
                    throw new JsonException("unbindable frame");
                }
            },
            cts.Token);

        await allFive.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await cts.CancelAsync();
        await run;

        Assert.Equal(["1", "2", "3", "4", "5"], seen);

        // The load-bearing assertion. A reconnect would have replayed nothing (the server sends its
        // five and stops), so the count above could be satisfied by a second connection redelivering
        // them only if the server accepted twice — and it must not have.
        Assert.Equal(1, server.Accepted);
    }

    // ── A message longer than the receive buffer ─────────────────────────
    [Fact]
    public async Task A_character_split_across_the_receive_buffer_survives_intact()
    {
        // ReceiveAsync fills a 64 KiB buffer and stops wherever it stops; it has no idea where
        // characters begin. Decoding each fragment on its own turns a character straddling that
        // boundary into replacement characters on BOTH sides of it — which is a JSON parse failure at
        // best and a silently different string at worst. Landed exactly: the '€' lead byte is the last
        // byte of the first read and its two continuation bytes are the first of the second.
        const int Boundary = 64 * 1024;
        var sent = new string('a', Boundary - 1) + "€" + new string('b', 32);
        var bytes = Encoding.UTF8.GetBytes(sent);
        Assert.Equal(0xE2, bytes[Boundary - 1]);
        Assert.Equal(0x82, bytes[Boundary]);

        using var server = new LoopbackWsServer(async (ws, ct) =>
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });

        var arrived = new TaskCompletionSource<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var conn = new WsConnection(server.Url, NullLogger.Instance, TimeProvider.System);
        var run = conn.RunAsync(_ => Task.CompletedTask, text => arrived.TrySetResult(text), cts.Token);

        var received = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await cts.CancelAsync();
        await run;

        Assert.DoesNotContain('�', received);
        Assert.Equal(sent, received);
    }

    // ── Which connection is the alarm talking about ──────────────────────
    [Fact]
    public void A_connection_that_delivered_frames_is_never_reported_as_silent()
    {
        // Sentry, 2026-09-06 19:34 UTC: "subscribed 566 symbols to @depth@100ms and received NOTHING in
        // 15s", about a connection that had delivered forty thousand frames. This is that sequence.
        var clock = new FakeTimeProvider(T0);
        var log = new ConnectionLog(clock);

        var first = log.Open(0);
        clock.Advance(TimeSpan.FromSeconds(13.8));

        // The drop, and the reconnect. Opening the successor is what zeroes the live frame counter —
        // so 1.2 s later, when connection 1's fifteen-second watcher finally wakes, the number it
        // reads is connection 2's nothing.
        var second = log.Open(40_000);
        clock.Advance(TimeSpan.FromSeconds(1.2));

        Assert.Equal(ConnectionVerdict.Replaced, log.Judge(first, framesReadBeforeThisCall: 0, connected: true));

        // And it can still say what connection 1 actually did, which is the whole point of asking.
        Assert.True(log.TryGet(first, out var run));
        Assert.Equal(40_000, run.Frames);
        Assert.NotNull(run.ClosedAt);
        Assert.Equal(13.8, (run.ClosedAt.Value - run.OpenedAt).TotalSeconds, 3);

        // Connection 2 is the live one and the reconnect rate is the symptom that had no detector.
        Assert.Equal(second, log.Current);
        Assert.Equal(2, log.CountOpenedWithin(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void The_four_verdicts_are_told_apart()
    {
        var clock = new FakeTimeProvider(T0);
        var log = new ConnectionLog(clock);
        var epoch = log.Open(0);

        Assert.Equal(ConnectionVerdict.Live, log.Judge(epoch, framesReadBeforeThisCall: 1, connected: true));

        // Open and empty. Only this one is evidence of a misrouted stream, and only this one is
        // allowed to send the reader to ws_url.
        Assert.Equal(ConnectionVerdict.Silent, log.Judge(epoch, framesReadBeforeThisCall: 0, connected: true));

        // Dropped and not yet reconnected: silence was never established, because the socket stopped
        // existing before the window closed.
        Assert.Equal(ConnectionVerdict.Dropped, log.Judge(epoch, framesReadBeforeThisCall: 0, connected: false));

        // A later connection exists, so nothing at all is known about this one's traffic — not even
        // when the counter handed in says zero, because that zero belongs to the successor.
        log.Open(0);
        Assert.Equal(ConnectionVerdict.Replaced, log.Judge(epoch, framesReadBeforeThisCall: 0, connected: true));
    }

    // ── A frame we cannot read costs one book, not the socket ────────────
    [Fact]
    public void A_depth_frame_that_will_not_bind_is_dropped_without_throwing()
    {
        // data.Deserialize<BinanceWsDepth> was the one unguarded parse on the hot path — JsonDocument.
        // Parse was guarded, this was not. A single venue frame with a non-string level threw straight
        // out of the receive loop, which ended it, which RunAsync logged as the venue dropping us.
        // Dropping the frame instead is safe here and only here: the next frame for that symbol carries
        // a pu that no longer matches, so the book goes dirty and gets reseeded without us having to
        // know which symbol was lost.
        var feed = NewFeed();

        // A well-formed envelope carrying a depthUpdate whose bid quantity is a NUMBER, not the string
        // the wire contract and the DTO both require.
        const string Unbindable =
            """{"stream":"btcusdt@depth@100ms","data":{"e":"depthUpdate","E":1,"T":1,"s":"BTCUSDT","U":2,"u":3,"pu":1,"b":[["100.0",5]],"a":[]}}""";

        Assert.Null(Record.Exception(() => feed.OnMessage(Unbindable)));

        // The neighbours it used to take down with it: a well-formed depthUpdate, the subscribe ack,
        // and a frame that is not JSON at all.
        const string Wellformed =
            """{"stream":"btcusdt@depth@100ms","data":{"e":"depthUpdate","E":1,"T":1,"s":"BTCUSDT","U":2,"u":3,"pu":1,"b":[["100.0","5"]],"a":[["101.0","5"]]}}""";

        Assert.Null(Record.Exception(() => feed.OnMessage(Wellformed)));
        Assert.Null(Record.Exception(() => feed.OnMessage("""{"result":null,"id":1}""")));
        Assert.Null(Record.Exception(() => feed.OnMessage("not json at all")));
    }

    private static BinanceWsFeed NewFeed()
    {
        var clock = new FakeTimeProvider(T0);
        return new BinanceWsFeed(
            "ws://localhost:1/", new BinanceUsdmClient("http://localhost:1/"),
            new VenueGate("BINANCE", 1, 1, clock), NullLoggerFactory.Instance, clock,
            staleAfter: TimeSpan.FromSeconds(30), crosscheckInterval: TimeSpan.FromMinutes(5), driftBps: 50);
    }

    /// <summary>A real WebSocket server on loopback. These tests are about what happens between
    /// <c>ReceiveAsync</c> calls and at buffer boundaries, and neither survives being mocked — the
    /// fragment boundary in particular exists only because a real socket decides where to stop.</summary>
    private sealed class LoopbackWsServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private int _accepted;

        public LoopbackWsServer(Func<WebSocket, CancellationToken, Task> session)
        {
            var port = FreePort();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Url = $"ws://localhost:{port}/";
            _ = AcceptAsync(session);
        }

        public string Url { get; }

        /// <summary>How many sockets this server handed out. One means no reconnect happened.</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task AcceptAsync(Func<WebSocket, CancellationToken, Task> session)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    var accepted = await context.AcceptWebSocketAsync(null);
                    Interlocked.Increment(ref _accepted);
                    _ = RunSessionAsync(session, accepted.WebSocket);
                }
            }
            catch (Exception)
            {
                // The listener was closed by Dispose, or the client went away. Either is the end of
                // the test, not a failure of it.
            }
        }

        private async Task RunSessionAsync(Func<WebSocket, CancellationToken, Task> session, WebSocket socket)
        {
            try
            {
                await session(socket, _cts.Token);
            }
            catch (Exception)
            {
                // Same: teardown, not a failure.
            }
            finally
            {
                socket.Dispose();
            }
        }
    }
}

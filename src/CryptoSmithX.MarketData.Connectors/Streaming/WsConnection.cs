using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Streaming;

/// <summary>
/// A resilient public WebSocket, venue-agnostic — reused by any streaming adapter. Owns the socket
/// and the connect → subscribe → read → reconnect lifecycle: exponential backoff with jitter on a
/// drop, and an idle watchdog that tears a silent socket down so it reconnects rather than hanging
/// forever. Message handling is the caller's; this only moves frames. The whole thing lives and dies
/// with the <see cref="CancellationToken"/> the supervisor passes, so disabling the exchange closes it.
/// </summary>
public sealed class WsConnection
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Uri _url;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private volatile ClientWebSocket? _socket;
    private long _lastReceivedTicks;

    public WsConnection(string url, ILogger logger, TimeProvider clock)
    {
        _url = new Uri(url);
        _logger = logger;
        _clock = clock;
    }

    public bool Connected => _socket?.State == WebSocketState.Open;

    /// <summary>Connect, (re)subscribe via <paramref name="onOpen"/>, then pump text frames to
    /// <paramref name="onMessage"/>, reconnecting until cancelled.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> onOpen, Action<string> onMessage, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var jitter = new Random();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(_url, ct);
                _socket = socket;
                _lastReceivedTicks = _clock.GetUtcNow().Ticks;
                backoff = TimeSpan.FromSeconds(1);
                _logger.LogInformation("WS connected to {Url}", _url);

                await onOpen(ct);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watchdog = WatchdogAsync(socket, linked.Token);
                try
                {
                    await ReceiveLoopAsync(socket, onMessage, ct);
                }
                finally
                {
                    linked.Cancel();
                    await watchdog;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WS connection to {Url} dropped", _url);
            }
            finally
            {
                _socket = null;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            var wait = backoff + TimeSpan.FromMilliseconds(jitter.Next(0, 500));
            try
            {
                await Task.Delay(wait, _clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>Send one text frame. No-op if the socket is not open — a dropped feed degrades to
    /// REST, it does not throw up the stack.</summary>
    public async Task SendAsync(string text, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        await _sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WS send failed");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, Action<string> onMessage, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var assembly = new StringBuilder();

        // A STATEFUL decoder, not Encoding.UTF8.GetString per fragment. ReceiveAsync fills the buffer
        // and stops; it has no idea where characters begin, so a message longer than 64 KB is split
        // wherever the 65 536th byte falls — which can be the middle of a multi-byte sequence. Decoding
        // each fragment independently turns that one character into replacement characters in BOTH
        // halves, and the JSON that contained it either fails to parse or parses into a string that is
        // silently not what the venue sent. Demonstrated on a real socket: a 65 556-byte message whose
        // first continuation byte lands on the boundary came back with three U+FFFD; the ASCII control
        // was clean. The decoder carries the partial sequence across the seam instead, and flushing on
        // EndOfMessage is what still reports a genuinely truncated tail rather than hiding it.
        //
        // Not currently reachable on Binance depth (909 B mean, pure ASCII) — this is here because the
        // buffer size, not the payload, decides when it fires, and the next stream added is not
        // required to be ASCII.
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        var handlerFaults = 0L;

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            _lastReceivedTicks = _clock.GetUtcNow().Ticks;

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            var written = decoder.GetChars(buffer, 0, result.Count, chars, 0, result.EndOfMessage);
            assembly.Append(chars, 0, written);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = assembly.ToString();
            assembly.Clear();

            // A HANDLER FAULT IS OURS, AND IT MUST NOT COST THE CONNECTION. Without this catch an
            // exception out of onMessage ends the receive loop, and RunAsync's outer handler logs it
            // as "WS connection to {Url} dropped" and reconnects — so one unbindable frame reads in
            // the logs as a venue-side disconnect, and a venue that keeps sending that frame reads as
            // a reconnect storm we would go looking for on the network. Reproduced: 11 frames
            // delivered, 11 reconnects in 12 s. Rethrowing on cancellation keeps shutdown a shutdown.
            try
            {
                onMessage(text);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                handlerFaults++;

                // The first one carries the stack; after that it is the RATE that matters and a
                // per-frame warning at ~2 000 frames a second would bury it.
                if (handlerFaults == 1 || handlerFaults % 1000 == 0)
                {
                    _logger.LogWarning(
                        ex, "WS to {Url}: message handler threw ({Faults} so far on this connection). The "
                        + "connection is HEALTHY and stays open; this frame is lost", _url, handlerFaults);
                }
            }
        }
    }

    // Abort a socket that has gone quiet past the idle timeout — ReceiveAsync then throws and the
    // outer loop reconnects. Cheaper and simpler than a per-message receive timeout on a firehose.
    private async Task WatchdogAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _clock, ct);
                var idle = _clock.GetUtcNow().Ticks - Interlocked.Read(ref _lastReceivedTicks);
                if (idle > IdleTimeout.Ticks)
                {
                    _logger.LogWarning("WS to {Url} idle beyond {Idle}s; aborting to reconnect", _url, IdleTimeout.TotalSeconds);
                    socket.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal — the receive loop ended and cancelled us.
        }
    }
}

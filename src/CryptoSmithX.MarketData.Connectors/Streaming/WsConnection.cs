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

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            _lastReceivedTicks = _clock.GetUtcNow().Ticks;

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            assembly.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = assembly.ToString();
            assembly.Clear();
            onMessage(text);
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

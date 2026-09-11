using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// The venue's own catalogue over its Socket.IO broadcast — open interest, funding, spreads and
/// trading hours, as they change.
///
/// <b>Socket.IO is a protocol ON a WebSocket, not a WebSocket.</b> <see cref="WsConnection"/> still
/// carries the transport (and with it the reconnect, the backoff and the idle timeout, which is the
/// hard part and is already solved); what sits on top of it here is Engine.IO v4 framing, verified
/// against the live server on 2026-09-11:
///
///   0{"sid":…,"pingInterval":25000}   the server's handshake, on connect
///   40                                 we join the default namespace
///   40{"sid":…}                        the server acknowledges it
///   2  →  3                            server pings, WE must answer or be dropped
///   42["RES:DATA",{…}]                 an event, and the only one this feed cares about
///
/// <b>Diffs, not snapshots.</b> The server sends no state on connect and replays nothing that was
/// missed, so every (re)connect bootstraps from <c>GET /v2/trading</c> and deep-merges each payload
/// into it. A feed that merged into nothing would serve a catalogue containing only whatever
/// happened to move since it attached.
///
/// No authentication and nothing to emit: the server broadcasts to everyone who connects.
/// </summary>
public sealed class AvantisCatalogFeed : IAvantisCatalogFeed
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WsConnection _conn;
    private readonly AvantisClient _client;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _staleAfter;

    /// <summary>The merged catalogue, replaced whole on every change so a reader never walks a
    /// half-merged one.</summary>
    private volatile JsonObject? _merged;

    private volatile AvantisCatalog? _catalog;
    private volatile Dictionary<string, string> _pythSymbols = new(StringComparer.Ordinal);

    public AvantisCatalogFeed(
        string url, AvantisClient client, ILoggerFactory loggers, TimeProvider clock, TimeSpan staleAfter)
    {
        _client = client;
        _clock = clock;
        _staleAfter = staleAfter;
        _log = loggers.CreateLogger("Avantis.Ws");
        _conn = new WsConnection(SocketUrl(url), _log, clock);
    }

    /// <summary>The Socket.IO endpoint on the data host, as a WebSocket. The path and the two query
    /// parameters are the protocol's, not a choice: Engine.IO 4 refuses anything else.</summary>
    internal static string SocketUrl(string baseUrl)
    {
        var host = baseUrl.TrimEnd('/');
        host = host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + host[8..]
             : host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + host[7..]
             : host;
        return $"{host}/socket.io/?EIO=4&transport=websocket";
    }

    public void Start(CancellationToken ct) => _ = RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await _conn.RunAsync(OnOpenAsync, OnMessage, ct);
        }
        catch (OperationCanceledException)
        {
            // The exchange was disabled or the process is going down.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Avantis catalogue feed stopped");
        }
    }

    /// <summary>
    /// Every connect starts by asking REST for the whole catalogue, because the socket will never
    /// send one. The namespace join goes out first so a diff arriving during the fetch is merged
    /// into a snapshot rather than dropped on the floor.
    /// </summary>
    private async Task OnOpenAsync(CancellationToken ct)
    {
        await _conn.SendAsync("40", ct);

        try
        {
            var raw = await _client.GetTradingRawAsync(ct);
            _merged = JsonNode.Parse(raw) as JsonObject;
            Publish();
            _log.LogInformation("Avantis catalogue: bootstrapped {Pairs} pairs over REST", PairCount());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without a snapshot the diffs mean nothing, so the feed simply has no catalogue and
            // the adapter falls back to its own REST call. Said out loud rather than served empty.
            _log.LogWarning(ex, "Avantis catalogue: bootstrap failed; the socket's diffs have nothing to merge into");
        }
    }

    private void OnMessage(string frame)
    {
        if (frame.Length == 0)
        {
            return;
        }

        // Engine.IO ping. Answering is not optional: the server drops a client that does not,
        // after pingTimeout, and a dropped socket here reads as a venue that went quiet.
        if (frame[0] == '2' && frame.Length == 1)
        {
            _ = _conn.SendAsync("3", CancellationToken.None);
            return;
        }

        if (!frame.StartsWith("42", StringComparison.Ordinal))
        {
            return;   // handshake, namespace ack, or an event type this build does not know
        }

        try
        {
            if (JsonNode.Parse(frame[2..]) is not JsonArray array || array.Count < 2
                || array[0]?.GetValue<string>() is not "RES:DATA"
                || array[1] is not JsonObject diff)
            {
                return;
            }

            var merged = _merged;
            if (merged is null)
            {
                return;   // nothing to merge into; the next reconnect bootstraps
            }

            // Copy-then-merge rather than mutating in place: a reader holding the previous object
            // keeps walking a whole catalogue instead of one being edited underneath it.
            var next = JsonNode.Parse(merged.ToJsonString()) as JsonObject;
            if (next is null)
            {
                return;
            }

            Merge(next, diff);
            _merged = next;
            Publish();
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Avantis catalogue: a frame this build cannot read");
        }
    }

    /// <summary>Deep merge, because the payloads are PARTIAL: a diff carries only the pairs that
    /// moved and only the fields that moved within them. Replacing wholesale would blank every
    /// field the venue did not happen to resend.</summary>
    internal static void Merge(JsonObject into, JsonObject diff)
    {
        foreach (var (key, value) in diff.ToList())
        {
            if (value is JsonObject nested && into[key] is JsonObject target)
            {
                Merge(target, nested);
                continue;
            }

            into[key] = value?.DeepClone();
        }
    }

    private void Publish()
    {
        var merged = _merged;
        if (merged is null)
        {
            return;
        }

        var catalog = new AvantisCatalog(merged.ToJsonString(), _clock.GetUtcNow());
        _catalog = catalog;

        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in catalog.Typed?.PairInfos?.Values ?? Enumerable.Empty<AvPair>())
        {
            if (p.Feed?.Attributes?.Symbol is { Length: > 0 } pyth)
            {
                symbols[AvantisMarketData.Symbol(p)] = pyth;
            }
        }

        _pythSymbols = symbols;
    }

    private int PairCount() => _catalog?.Typed?.PairInfos?.Count ?? 0;

    /// <summary>Serves the merged catalogue only while it is genuinely fresh — the central honesty
    /// rule of every WS path here. A socket that stopped sending diffs looks identical to a market
    /// that stopped moving, so the age is what tells them apart.</summary>
    public bool TryGetCatalog(out AvantisCatalog? catalog)
    {
        catalog = _catalog;
        return catalog is not null && _clock.GetUtcNow() - catalog.At <= _staleAfter;
    }

    public bool TryGetPythSymbol(string exchangeSymbol, out string? pythSymbol)
    {
        var found = _pythSymbols.TryGetValue(exchangeSymbol, out var symbol);
        pythSymbol = symbol;
        return found;
    }
}

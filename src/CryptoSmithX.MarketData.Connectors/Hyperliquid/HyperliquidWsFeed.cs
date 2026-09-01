using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>
/// The live Hyperliquid book over WebSocket: subscribes <c>l2Book</c> for every live coin, one
/// subscribe message per coin (the venue has no batched subscribe, unlike Kraken's product_ids array).
/// Confirmed live during recon: every <c>l2Book</c> push is a FULL snapshot of the book, not a delta —
/// so unlike <see cref="Kraken.KrakenBookBuilder"/> there is no seq machinery here at all; a message
/// simply replaces the cached top and depth for its coin. That is a property of the protocol, not a
/// shortcut: it also means there is no "gap" failure mode to detect, only socket death (handled by
/// <see cref="WsConnection"/>'s idle watchdog) and per-symbol staleness (handled by
/// <see cref="MarketCache{T}"/>). A REST cross-check against <c>metaAndAssetCtxs.midPx</c> still guards
/// against a book that has silently frozen behind a socket that itself still looks alive.
/// </summary>
public sealed class HyperliquidWsFeed : IHyperliquidLiveFeed
{
    private static readonly TimeSpan SubscriptionRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResyncDebounce = TimeSpan.FromSeconds(5);

    private readonly WsConnection _conn;
    private readonly HyperliquidClient _client;
    private readonly MarketCache<(BookTop Top, Depth? Depth)> _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _crosscheckInterval;
    private readonly int _driftBps;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastResync = new(StringComparer.Ordinal);
    private CancellationToken _ct;

    private volatile string[] _symbols = [];

    public HyperliquidWsFeed(
        string wsUrl, HyperliquidClient client, ILoggerFactory loggers, TimeProvider clock,
        TimeSpan staleAfter, TimeSpan crosscheckInterval, int driftBps)
    {
        _client = client;
        _clock = clock;
        _log = loggers.CreateLogger("Hyperliquid.Ws");
        _conn = new WsConnection(wsUrl, loggers.CreateLogger("Hyperliquid.Ws.Conn"), clock);
        _cache = new MarketCache<(BookTop, Depth?)>(clock);
        _staleAfter = staleAfter;
        _crosscheckInterval = crosscheckInterval;
        _driftBps = driftBps;
    }

    public void Start(CancellationToken ct)
    {
        _ct = ct;
        _ = RunAsync(ct);
    }

    public bool TryGetTop(string symbol, out BookTop top)
    {
        if (Healthy && _cache.TryGet(symbol, _staleAfter, out var entry))
        {
            top = entry.Top;
            return true;
        }

        top = default!;
        return false;
    }

    public bool TryGetDepth(string symbol, out Depth depth)
    {
        if (Healthy && _cache.TryGet(symbol, _staleAfter, out var entry) && entry.Depth is not null)
        {
            depth = entry.Depth;
            return true;
        }

        depth = default!;
        return false;
    }

    private bool Healthy => _conn.Connected && _cache.FreshCount(_staleAfter) >= Math.Max(1, _symbols.Length / 2);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshSymbolsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Hyperliquid WS: initial coin list fetch failed; starting empty, will refresh");
        }

        await Task.WhenAll(
            _conn.RunAsync(OnOpenAsync, OnMessage, ct),
            LoopAsync(RefreshSymbolsAsync, SubscriptionRefresh, "subscription refresh", ct),
            LoopAsync(CrosscheckAsync, _crosscheckInterval, "cross-check", ct));
    }

    private async Task OnOpenAsync(CancellationToken ct)
    {
        var symbols = _symbols;
        _log.LogInformation("Hyperliquid WS: subscribing {Count} coins", symbols.Length);
        foreach (var coin in symbols)
        {
            await SendSubscriptionAsync("subscribe", coin, ct);
        }
    }

    private void OnMessage(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("channel", out var ch) || ch.GetString() != "l2Book")
            {
                return;
            }

            if (!root.TryGetProperty("data", out var data))
            {
                return;
            }

            var msg = data.Deserialize<HlL2BookWsMessage>(HyperliquidJson.Options);
            if (msg is null || string.IsNullOrEmpty(msg.Coin))
            {
                return;
            }

            var at = msg.Time > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(msg.Time) : _clock.GetUtcNow();
            var (top, depth) = HyperliquidBookMath.Compute(new HlL2Book { Levels = msg.Levels }, at);
            if (top is not null)
            {
                _cache.Set(msg.Coin, (top, depth));
            }
        }
    }

    private async Task CrosscheckAsync(CancellationToken ct)
    {
        var (meta, ctxs) = await _client.GetMetaAndAssetCtxsAsync(ct);
        var drifted = 0;
        for (var i = 0; i < meta.Universe.Count && i < ctxs.Count; i++)
        {
            var symbol = meta.Universe[i].Name;
            if (ctxs[i].MidPx is not { } midText || !double.TryParse(midText, System.Globalization.CultureInfo.InvariantCulture, out var restMid) || restMid <= 0)
            {
                continue;
            }

            if (!_cache.TryGet(symbol, _staleAfter, out var entry))
            {
                continue;
            }

            var wsMid = (entry.Top.BidPrice + entry.Top.AskPrice) / 2;
            if (wsMid <= 0)
            {
                continue;
            }

            var driftBps = Math.Abs(restMid - wsMid) / restMid * 10_000.0;
            if (driftBps > _driftBps)
            {
                drifted++;
                _cache.Remove(symbol);   // dirty — forces TryGet* to miss until a fresh push refills it
                _ = ResubscribeAsync(symbol);
            }
        }

        if (drifted > 0)
        {
            _log.LogWarning("Hyperliquid WS cross-check: {Count} coins drifted past {Bps} bps; resubscribing", drifted, _driftBps);
        }
    }

    /// <summary>Nudges a coin that looks frozen: unsubscribe then resubscribe forces the venue to push
    /// a fresh full snapshot, since (unlike Kraken) there is no delta stream to simply resync.</summary>
    private async Task ResubscribeAsync(string symbol)
    {
        var now = _clock.GetUtcNow();
        if (_lastResync.TryGetValue(symbol, out var last) && now - last < ResyncDebounce)
        {
            return;
        }

        _lastResync[symbol] = now;
        await SendSubscriptionAsync("unsubscribe", symbol, _ct);
        await SendSubscriptionAsync("subscribe", symbol, _ct);
    }

    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        var meta = await _client.GetMetaAsync(ct);
        var next = meta.Universe
            .Where(u => !u.IsDelisted)
            .Select(u => u.Name)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var prev = _symbols;
        _symbols = next;

        if (!_conn.Connected)
        {
            return;   // OnOpen will subscribe the whole set on connect
        }

        var added = next.Except(prev, StringComparer.Ordinal);
        var removed = prev.Except(next, StringComparer.Ordinal);
        foreach (var coin in added)
        {
            await SendSubscriptionAsync("subscribe", coin, ct);
        }

        foreach (var coin in removed)
        {
            await SendSubscriptionAsync("unsubscribe", coin, ct);
            _cache.Remove(coin);
        }
    }

    private Task SendSubscriptionAsync(string method, string coin, CancellationToken ct) =>
        _conn.SendAsync(
            JsonSerializer.Serialize(new { method, subscription = new { type = "l2Book", coin } }, HyperliquidJson.Options),
            ct);

    private async Task LoopAsync(Func<CancellationToken, Task> body, TimeSpan interval, string what, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _clock, ct);
                await body(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hyperliquid WS {What} pass failed", what);
            }
        }
    }
}

internal sealed record HlL2BookWsMessage
{
    public string Coin { get; init; } = "";
    public long Time { get; init; }
    public List<List<HlLevel>> Levels { get; init; } = [];
}

internal static class HyperliquidJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>
/// The live Hyperliquid market over WebSocket: subscribes <c>l2Book</c>, <c>activeAssetCtx</c> and
/// <c>candle</c> for every live coin — one subscribe message per coin per channel, since the venue
/// has no batched subscribe of any kind, unlike Kraken's product_ids array or Binance's <c>@arr</c>
/// streams (captured live, see Fixtures/hyperliquid-ws/README.md).
///
/// <c>l2Book</c>: confirmed live during recon, every push is a FULL snapshot of the book, not a
/// delta — so unlike <see cref="Kraken.KrakenBookBuilder"/> there is no seq machinery here at all; a
/// message simply replaces the cached top and depth for its coin. That is a property of the
/// protocol, not a shortcut: it also means there is no "gap" failure mode to detect, only socket
/// death (handled by <see cref="WsConnection"/>'s idle watchdog) and per-symbol staleness (handled by
/// <see cref="MarketCache{T}"/>). A REST cross-check against <c>metaAndAssetCtxs.midPx</c> still
/// guards against a book that has silently frozen behind a socket that itself still looks alive.
///
/// <c>activeAssetCtx</c>: the exact field set of <c>metaAndAssetCtxs</c>'s per-coin context (mark,
/// oracle, funding, open interest, mid), pushed continuously per coin rather than batched — captured
/// live, 16 pushes for one coin in 15 s of an otherwise quiet market, so this is not an on-change-
/// only stream.
///
/// <c>candle</c>: unlike WEEX's <c>klineSnapshot</c>, a (re)subscribe here seeds NO history at all —
/// the first frame is already just the live forming bar (captured live, see the fixtures README).
/// <see cref="CandleCache.TryGetRange"/>'s own "refuse a partial answer" rule is what keeps that
/// honest: a range reaching further back than "since this connection last subscribed" simply fails
/// and the caller falls to REST for the whole request.
/// </summary>
public sealed class HyperliquidWsFeed : IHyperliquidLiveFeed
{
    private static readonly TimeSpan SubscriptionRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResyncDebounce = TimeSpan.FromSeconds(5);

    private readonly WsConnection _conn;
    private readonly HyperliquidClient _client;
    private readonly MarketCache<(BookTop Top, Depth? Depth)> _cache;
    private readonly MarketCache<AssetContext> _contexts;
    private readonly CandleCache _candles = new();
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
        _contexts = new MarketCache<AssetContext>(clock);
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

    /// <summary>Every coin's context this feed currently trusts, whole-batch — the same ternary
    /// Kraken's ticker cache uses: false means "the feed as a whole is not healthy enough, go do the
    /// REST call instead", never a partial list stitched from stale and fresh entries. Gated on its
    /// own freshness count rather than the book's: <c>l2Book</c> and <c>activeAssetCtx</c> are
    /// independent per-coin subscriptions on the same socket, and a majority-fresh book says nothing
    /// about whether the context stream is keeping up.</summary>
    public bool TryGetFreshContexts(out IReadOnlyList<AssetContext> contexts)
    {
        if (!ContextsHealthy)
        {
            contexts = [];
            return false;
        }

        contexts = _contexts.FresherThan(_staleAfter);
        return true;
    }

    /// <summary>Gated on connection state, not on <see cref="ContextsHealthy"/> or
    /// <see cref="Healthy"/>: <see cref="CandleCache.TryGetRange"/> already refuses anything less
    /// than a fully-covered range on its own — a per-request guarantee, stronger than a feed-wide
    /// freshness count could add. What connection state alone would miss is a stale
    /// currently-forming bar served as live across a drop that never triggered a resubscribe;
    /// <see cref="RefreshSymbolsAsync"/> and <see cref="OnOpenAsync"/> re-subscribe every coin's
    /// candle channel — which reseeds it, per the class remarks — on every reconnect, closing that
    /// case the same way <see cref="Weex.WeexWsFeed"/> does for depth.</summary>
    public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
    {
        if (!_conn.Connected)
        {
            candles = [];
            return false;
        }

        return _candles.TryGetRange(symbol, from, to, out candles);
    }

    private bool Healthy => _conn.Connected && _cache.FreshCount(_staleAfter) >= Math.Max(1, _symbols.Length / 2);

    private bool ContextsHealthy => _conn.Connected && _contexts.FreshCount(_staleAfter) >= Math.Max(1, _symbols.Length / 2);

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
        _log.LogInformation(
            "Hyperliquid WS: subscribing {Count} coins to l2Book, activeAssetCtx and candle", symbols.Length);
        foreach (var coin in symbols)
        {
            await SubscribeCoinAsync("subscribe", coin, ct);
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
            if (!root.TryGetProperty("channel", out var ch) || !root.TryGetProperty("data", out var data))
            {
                return;
            }

            switch (ch.GetString())
            {
                case "l2Book":
                    HandleL2Book(data);
                    break;
                case "activeAssetCtx":
                    HandleActiveAssetCtx(data);
                    break;
                case "candle":
                    HandleCandle(data);
                    break;
            }
        }
    }

    private void HandleL2Book(JsonElement data)
    {
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

    /// <summary><c>data</c> is <c>{"coin":"BTC","ctx":{...}}</c> — captured live, see
    /// Fixtures/hyperliquid-ws/README.md. <c>ctx</c>'s field set matches <see cref="HlAssetCtx"/>
    /// exactly under the client's ordinary case-insensitive options (no <c>t</c>/<c>T</c> collision
    /// here, unlike the candle DTO), so it deserialises straight into the existing REST record —
    /// see <see cref="HyperliquidClient"/>'s remarks on why that record has no explicit property
    /// names.</summary>
    private void HandleActiveAssetCtx(JsonElement data)
    {
        if (!data.TryGetProperty("coin", out var coinEl) || coinEl.GetString() is not { } coin
            || string.IsNullOrEmpty(coin))
        {
            return;
        }

        if (!data.TryGetProperty("ctx", out var ctxEl))
        {
            return;
        }

        var ctx = ctxEl.Deserialize<HlAssetCtx>(HyperliquidJson.Options);
        if (ctx is null)
        {
            return;
        }

        // No last-trade-price field exists at this scale on Hyperliquid, REST or WS: the book mid
        // is the closest honest proxy the venue offers at all — same reasoning as GetTickersAsync's
        // REST path, restated here because this is now a second place that composes the same claim.
        if (ctx.MidPx is not { } midText || !double.TryParse(midText, System.Globalization.CultureInfo.InvariantCulture, out var mid) || mid <= 0)
        {
            return;
        }

        if (!double.TryParse(ctx.MarkPx, System.Globalization.CultureInfo.InvariantCulture, out var mark)
            || !double.TryParse(ctx.OraclePx, System.Globalization.CultureInfo.InvariantCulture, out var oracle)
            || !double.TryParse(ctx.Funding, System.Globalization.CultureInfo.InvariantCulture, out var funding)
            || !double.TryParse(ctx.DayNtlVlm, System.Globalization.CultureInfo.InvariantCulture, out var turnover)
            || !double.TryParse(ctx.OpenInterest, System.Globalization.CultureInfo.InvariantCulture, out var oi))
        {
            return;
        }

        var at = _clock.GetUtcNow();
        _contexts.Set(coin, new AssetContext(coin, mid, mark, oracle, funding, turnover, oi, at));
    }

    /// <summary><c>data</c> is <c>{"t","T","s","i","o","c","h","l","v","n"}</c> — the same field set
    /// as the REST <c>candleSnapshot</c> row (<see cref="HlCandle"/>), captured live. Deserialised
    /// with the client's existing <c>CandleJson</c> options (case-sensitive — <c>t</c>/<c>T</c>
    /// would otherwise collide), exactly as the REST path already does.</summary>
    private void HandleCandle(JsonElement data)
    {
        if (!data.TryGetProperty("s", out var coinEl) || coinEl.GetString() is not { } coin
            || string.IsNullOrEmpty(coin))
        {
            return;
        }

        var bar = data.Deserialize<HlCandle>(HyperliquidClient.CandleJson);
        if (bar is null)
        {
            return;
        }

        _candles.Update(coin, new Candle(
            coin, DateTimeOffset.FromUnixTimeMilliseconds(bar.OpenTimeMs),
            Parse(bar.Open), Parse(bar.High), Parse(bar.Low), Parse(bar.Close), Parse(bar.Volume), bar.TradeCount));
    }

    private static double Parse(string value) => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

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
        // All three channels, not only l2Book: unsubscribe/subscribe is the only way this venue
        // offers to force a fresh push at all, and re-seeding candle/context history alongside the
        // book this coin's cross-check actually cares about is a harmless resend, not a second
        // subscription to track separately — the same trade WeexWsFeed makes for its own resync.
        await SubscribeCoinAsync("unsubscribe", symbol, _ct);
        await SubscribeCoinAsync("subscribe", symbol, _ct);
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
            await SubscribeCoinAsync("subscribe", coin, ct);
        }

        foreach (var coin in removed)
        {
            await SubscribeCoinAsync("unsubscribe", coin, ct);
            _cache.Remove(coin);
            _contexts.Remove(coin);
            _candles.Remove(coin);
        }
    }

    /// <summary>All three channels for one coin — <c>l2Book</c>, <c>activeAssetCtx</c>, <c>candle</c>
    /// — as three separate subscribe messages, since the venue has no batched subscribe of any kind
    /// (confirmed live: one message per coin per channel, no array form accepted).</summary>
    private async Task SubscribeCoinAsync(string method, string coin, CancellationToken ct)
    {
        await SendSubscriptionAsync(method, new { type = "l2Book", coin }, ct);
        await SendSubscriptionAsync(method, new { type = "activeAssetCtx", coin }, ct);
        await SendSubscriptionAsync(method, new { type = "candle", coin, interval = "1m" }, ct);
    }

    private Task SendSubscriptionAsync(string method, object subscription, CancellationToken ct) =>
        _conn.SendAsync(JsonSerializer.Serialize(new { method, subscription }, HyperliquidJson.Options), ct);

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

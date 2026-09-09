using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// The live Binance USDⓈ-M ticker and candles over WebSocket — the SECOND socket,
/// <c>wss://fstream.binance.com/market/stream</c>, deliberately separate from
/// <see cref="BinanceWsFeed"/>'s <c>/public/stream</c>: verified live that a stream lives on ONE
/// routed path and answers silence on the other, so depth and these three streams cannot share a
/// connection at all (see Fixtures/binance-market-ws/README.md).
///
/// THE ENVELOPE IS DIFFERENT FROM /public/stream. This is a combined-stream endpoint: every frame is
/// <c>{"stream":"&lt;name&gt;","data":...}</c>, never the bare payload <see cref="BinanceWsFeed"/>
/// reads.
///
/// !ticker@arr AND !markPrice@arr@1s ARE SHARDED, NOT ONE PUSH PER SECOND. Captured live: two
/// recurring array sizes (744 and 191 entries) rather than one atomic whole-venue snapshot — the
/// 191-entry shard held metals/equity-tokenized perpetuals, the 744-entry shard the crypto
/// derivatives universe. Every entry is therefore merged into a per-symbol cache one at a time,
/// exactly the pattern <see cref="MarketCache{T}"/> already exists for, and every entry is filtered
/// against the same in-scope symbol set <see cref="BinanceWsFeed"/> already computes from
/// <see cref="BinanceMarkets.IsInScope"/> — the sharded arrays mix in COIN-M and USDC-margined
/// symbols this segment does not track.
///
/// kline_1m IS per-symbol, unlike the two array streams, and needs the usual subscribe/unsubscribe
/// diffing as the tracked symbol set changes — the same shape <see cref="Weex.WeexWsFeed"/> already
/// uses for its own kline channel, sharing <see cref="CandleCache"/> with it.
/// </summary>
public sealed class BinanceMarketWsFeed : IBinanceMarketFeed
{
    private static readonly TimeSpan SubscriptionRefresh = TimeSpan.FromMinutes(5);

    /// <summary>Same chunk Binance's depth socket uses (<see cref="BinanceWsFeed.SubscribeChunk"/>) —
    /// not independently re-measured on this path, since the venue's incoming-message rate limit
    /// (10/s per connection) is a property of the connection, not the stream.</summary>
    private const int SubscribeChunk = 100;

    private static readonly TimeSpan SubscribePause = TimeSpan.FromMilliseconds(200);

    /// <summary>How long after subscribing a silent socket is reported. A lighter check than
    /// <see cref="BinanceWsFeed.WatchStartupLivenessAsync"/>'s epoch-tracked version: this feed's
    /// blast radius on staying silent is a coarser ticker/candle cadence, not "no depth at all", so
    /// a single per-connect flag is enough — it does not need to distinguish "replaced" from
    /// "silent" the way depth's reconnect-storm history forced that class to.</summary>
    private static readonly TimeSpan StartupLiveness = TimeSpan.FromSeconds(15);

    private readonly WsConnection _conn;
    private readonly BinanceUsdmClient _client;
    private readonly MarketCache<(double Last, double Turnover24h)> _ticker;
    private readonly MarketCache<(double Mark, double Index, double Funding)> _markPrice;
    private readonly CandleCache _candles = new();
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private volatile string[] _symbols = [];
    // O(1) membership for the per-frame filter below — Array.IndexOf over ~665 symbols, run for
    // every one of ~744 array entries on every markPrice push (roughly once a second), would be
    // the kind of cost that never shows up in a code review and always shows up in a profiler.
    private volatile HashSet<string> _known = new(StringComparer.Ordinal);
    private long _framesThisConnection;

    public BinanceMarketWsFeed(string wsUrl, BinanceUsdmClient client, ILoggerFactory loggers, TimeProvider clock)
    {
        _client = client;
        _clock = clock;
        _log = loggers.CreateLogger("Binance.Market");
        _conn = new WsConnection(wsUrl, loggers.CreateLogger("Binance.Market.Conn"), clock);
        _ticker = new MarketCache<(double, double)>(clock);
        _markPrice = new MarketCache<(double, double, double)>(clock);
    }

    public void Start(CancellationToken ct) => _ = RunAsync(ct);

    /// <summary>Whole-batch, same ternary Kraken's ticker cache uses: a symbol needs a fresh entry in
    /// BOTH caches to produce one context row — <c>!ticker@arr</c> alone has no mark/funding, and
    /// <c>!markPrice@arr</c> alone has no last price or turnover.</summary>
    public bool TryGetFreshContexts(out IReadOnlyList<BinanceContext> contexts)
    {
        if (!_conn.Connected)
        {
            contexts = [];
            return false;
        }

        var freshTickers = _ticker.FresherThan(StaleAfter);
        if (freshTickers.Count == 0)
        {
            contexts = [];
            return false;
        }

        // FresherThan on MarketCache<T> returns values only, so the symbol has to travel with a
        // second pass over the known set rather than a dictionary the cache does not expose.
        var now = _clock.GetUtcNow();
        var list = new List<BinanceContext>(freshTickers.Count);
        foreach (var symbol in _symbols)
        {
            if (!_ticker.TryGet(symbol, StaleAfter, out var t) || !_markPrice.TryGet(symbol, StaleAfter, out var m))
            {
                continue;
            }

            list.Add(new BinanceContext(symbol, t.Last, m.Mark, m.Index, m.Funding, t.Turnover24h, now));
        }

        if (list.Count == 0)
        {
            contexts = [];
            return false;
        }

        contexts = list;
        return true;
    }

    /// <summary>Gated on connection state only — <see cref="CandleCache.TryGetRange"/> already
    /// refuses a partial range on its own, a stronger per-request guarantee than a feed-wide count.
    /// A reconnect resubscribes every tracked symbol's kline channel (see <see cref="OnOpenAsync"/>),
    /// closing the "stale forming bar served as live" case the same way <see cref="Weex.WeexWsFeed"/>
    /// does.</summary>
    public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
    {
        if (!_conn.Connected)
        {
            candles = [];
            return false;
        }

        return _candles.TryGetRange(symbol, from, to, out candles);
    }

    /// <summary>How stale a cached entry may be and still be served — several times the venue's own
    /// push cadence (1 s for markPrice; ticker pushes on every 24h-window recompute, effectively
    /// continuous on a liquid symbol), the same "notice the stream stopped, not police its normal
    /// lag" reasoning <see cref="Weex.WeexOpenInterestFeed.MaxAge"/> documents for its own cache.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshSymbolsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Binance market feed: initial symbol fetch failed; starting empty, will refresh");
        }

        await Task.WhenAll(
            _conn.RunAsync(OnOpenAsync, OnMessage, ct),
            LoopAsync(RefreshSymbolsAsync, SubscriptionRefresh, "subscription refresh", ct));
    }

    private async Task OnOpenAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _framesThisConnection, 0);

        var symbols = _symbols;
        _log.LogInformation(
            "Binance market feed: subscribing !ticker@arr, !markPrice@arr@1s and {Count} kline_1m streams",
            symbols.Length);

        // The two array streams cover the whole venue in one subscribe each — no per-symbol
        // management, unlike kline. Sent first so a slow kline subscribe chunk-out never delays the
        // cheaper, more valuable streams.
        await SendAsync(new { method = "SUBSCRIBE", @params = new[] { "!ticker@arr", "!markPrice@arr@1s" }, id = NextId() }, ct);
        await SubscribeKlinesAsync("SUBSCRIBE", symbols, ct);

        _ = WatchStartupLivenessAsync(symbols.Length, ct);
    }

    /// <summary>The one detector this venue permits for a misrouted stream: the subscribe ack is
    /// <c>{"result":null}</c> whether or not the path was right, so only the absence of frames says
    /// so — see <see cref="BinanceWsFeed.WatchStartupLivenessAsync"/>, whose fuller epoch-tracked
    /// version this deliberately simplifies (see the class remarks on <see cref="StartupLiveness"/>).
    /// </summary>
    private async Task WatchStartupLivenessAsync(int subscribed, CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupLiveness, _clock, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_conn.Connected || Interlocked.Read(ref _framesThisConnection) > 0)
        {
            return;
        }

        _log.LogError(
            "Binance market feed: subscribed {Count} kline streams plus the two array streams, is STILL "
            + "OPEN, and has received NOTHING in {Seconds}s. The subscribe ack succeeds on a misrouted "
            + "stream on this venue — check that this segment's market-stream URL is /market/stream, not "
            + "/public/stream. Ticker and candles stay on REST until frames arrive.",
            subscribed, StartupLiveness.TotalSeconds);
    }

    private void OnMessage(string text)
    {
        Interlocked.Increment(ref _framesThisConnection);

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
            if (!root.TryGetProperty("stream", out var streamEl) || !root.TryGetProperty("data", out var data))
            {
                return;   // the SUBSCRIBE ack ({"result":null,"id":N}) — not a data frame
            }

            switch (streamEl.GetString())
            {
                case "!ticker@arr":
                    HandleTickerArray(data);
                    break;
                case "!markPrice@arr@1s":
                    HandleMarkPriceArray(data);
                    break;
                default:
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("e", out var e) && e.GetString() == "kline")
                    {
                        HandleKline(data);
                    }

                    break;
            }
        }
    }

    /// <summary>Each entry is <c>{s,c,q,...}</c> — <c>c</c> last price, <c>q</c> quote-asset volume
    /// (this dataset's Turnover24h), both decimal strings. Symbols outside our scope (COIN-M,
    /// USDC-margined — the array mixes them in) are dropped by the known-symbols check.</summary>
    private void HandleTickerArray(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var known = _known;
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("s", out var sEl) || sEl.GetString() is not { } symbol
                || !known.Contains(symbol))
            {
                continue;
            }

            if (!TryParseString(entry, "c", out var last) || !TryParseString(entry, "q", out var turnover))
            {
                continue;
            }

            _ticker.Set(symbol, (last, turnover));
        }
    }

    /// <summary>Each entry is <c>{s,p,i,r,...}</c> — <c>p</c> mark price, <c>i</c> index price,
    /// <c>r</c> funding rate, all decimal strings.</summary>
    private void HandleMarkPriceArray(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var known = _known;
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("s", out var sEl) || sEl.GetString() is not { } symbol
                || !known.Contains(symbol))
            {
                continue;
            }

            if (!TryParseString(entry, "p", out var mark) || !TryParseString(entry, "i", out var index)
                || !TryParseString(entry, "r", out var funding))
            {
                continue;
            }

            _markPrice.Set(symbol, (mark, index, funding));
        }
    }

    /// <summary><c>data</c> is <c>{"e":"kline","s":"BTCUSDT","k":{"t",...}}</c>, a single bar, not
    /// batched. <c>k.x</c> (a boolean: is this bar closed) is not read — this cache keys on open
    /// time regardless of closure, exactly as the WEEX and Hyperliquid kline caches do, and a
    /// still-forming bar is exactly what the REST path already tolerates writing (see
    /// <c>CandleCollector</c>'s comment on re-asking for the newest stored minute).</summary>
    private void HandleKline(JsonElement data)
    {
        if (!data.TryGetProperty("s", out var sEl) || sEl.GetString() is not { } symbol)
        {
            return;
        }

        if (!data.TryGetProperty("k", out var k))
        {
            return;
        }

        if (!k.TryGetProperty("t", out var tEl) || !tEl.TryGetInt64(out var t))
        {
            return;
        }

        if (!TryParseString(k, "o", out var open) || !TryParseString(k, "h", out var high)
            || !TryParseString(k, "l", out var low) || !TryParseString(k, "c", out var close)
            || !TryParseString(k, "v", out var volume))
        {
            return;
        }

        int? tradeCount = k.TryGetProperty("n", out var nEl) && nEl.TryGetInt32(out var n) ? n : null;

        _candles.Update(symbol, new Candle(
            symbol, DateTimeOffset.FromUnixTimeMilliseconds(t), open, high, low, close, volume, tradeCount));
    }

    private static bool TryParseString(JsonElement obj, string property, out double value)
    {
        value = 0;
        return obj.TryGetProperty(property, out var el)
            && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        try
        {
            var symbols = await _client.GetSymbolsAsync(ct);
            var next = symbols
                .Where(s => BinanceMarkets.IsInScope(s) && s.Status == "TRADING")
                .Select(s => s.Symbol)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            var prev = _symbols;
            _symbols = next;
            _known = new HashSet<string>(next, StringComparer.Ordinal);

            if (!_conn.Connected)
            {
                return;   // OnOpen will subscribe the whole set on connect
            }

            var added = next.Except(prev, StringComparer.Ordinal).ToArray();
            var removed = prev.Except(next, StringComparer.Ordinal).ToArray();
            if (added.Length > 0)
            {
                await SubscribeKlinesAsync("SUBSCRIBE", added, ct);
            }

            if (removed.Length > 0)
            {
                await SubscribeKlinesAsync("UNSUBSCRIBE", removed, ct);
                foreach (var symbol in removed)
                {
                    _candles.Remove(symbol);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Binance market feed: refreshing the symbol list failed; keeping the previous set");
        }
    }

    /// <summary>Chunked and paced like <see cref="BinanceWsFeed.SubscribeAsync"/> — the venue caps
    /// incoming messages at 10/s per connection regardless of which streams they name.</summary>
    private async Task SubscribeKlinesAsync(string method, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        for (var i = 0; i < symbols.Count; i += SubscribeChunk)
        {
            var chunk = symbols.Skip(i).Take(SubscribeChunk)
                .Select(s => s.ToLowerInvariant() + "@kline_1m")
                .ToArray();
            if (chunk.Length == 0)
            {
                continue;
            }

            await SendAsync(new { method, @params = chunk, id = NextId() }, ct);

            if (i + SubscribeChunk < symbols.Count)
            {
                await Task.Delay(SubscribePause, _clock, ct);
            }
        }
    }

    private long _nextId;

    private long NextId() => Interlocked.Increment(ref _nextId);

    private Task SendAsync(object frame, CancellationToken ct) =>
        _conn.SendAsync(JsonSerializer.Serialize(frame), ct);

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
                _log.LogWarning(ex, "Binance market feed {What} pass failed", what);
            }
        }
    }
}

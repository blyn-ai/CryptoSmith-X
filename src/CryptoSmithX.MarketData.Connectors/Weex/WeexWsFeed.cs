using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// The live WEEX order book over WebSocket (contract V3, <c>wss://ws-contract.weex.com/v3/ws/public</c>):
/// subscribes <c>@depth200</c> for every symbol with a real market, maintains the books, and serves
/// the adapter depth only while the feed is genuinely healthy. Same shape as
/// <see cref="Kraken.KrakenWsFeed"/> — a <see cref="WsConnection"/>, a builder, a subscription-refresh
/// loop and a REST cross-check, all driven by the supervisor's token — with the WEEX-specific parts
/// called out below rather than smuggled in.
///
/// WHY THIS EXISTS. WEEX has no batched depth endpoint in either API generation, so the REST sweep
/// pays one round trip per symbol: 361 s per pass over ~1005 instruments against a 60 s interval, on
/// an idle host. Depth is also the one thing in this system that cannot be re-fetched later at any
/// price — the book as it stood at 12:04:31 is gone. The socket replaces a sweep that cannot keep up
/// with a stream that arrives as the book changes.
///
/// WHY IT IS NOT THE OLD DEFERRAL. Commit 100f605 declined a WEEX WS feed because V2 chained deltas
/// through startVersion/endVersion. WEEX launched contract V3 on 2026-03-18 and retired V2; V3 is a
/// Binance-shaped U/u protocol and, unlike Binance, delivers the seeding snapshot on the socket
/// itself, so there is no REST-seed race at all. That is not taken on faith here: the protocol is
/// captured in Fixtures/weex-ws and asserted by WeexWsProtocolTests, precisely because the last
/// conclusion about this venue lived only in a commit message and went stale in silence.
///
/// THREE THINGS THAT ARE NOT KRAKEN'S:
///
///   1. The sequencing rule — an exact <c>U == previous u</c> equality. See
///      <see cref="WeexBookBuilder"/>, which is where it is enforced and explained.
///   2. Two symbol spellings. Stored identity is v2 ('cmt_btcusdt'); the socket speaks v3
///      ('BTCUSDT'). The map is built from the venue's own symbol list, never from a guessed
///      inverse transform (see <see cref="WeexMarkets.ToV3Symbol"/>).
///   3. A rejected subscribe is dropped, never retried. Six rejects on one connection close it with
///      <c>1007 Unrecognized message sent multiple times</c> (captured twice, threshold six both
///      times), which would take every other subscription on that socket down with it. A retry loop
///      over an unknown channel is therefore not a wasted request here, it is an outage.
/// </summary>
public sealed class WeexWsFeed : IWeexLiveFeed
{
    /// <summary>
    /// The subscribed depth channel and the level it promises. 200 rather than the default 15
    /// because <see cref="Kraken.DepthMath"/> nulls any band the book does not reach past, and 15
    /// levels on a liquid symbol span about 5 bps — a 15-level WS book would answer null for the 25
    /// and 50 bps bands that the REST path (limit=200) fills in, and the adapter prefers the socket,
    /// so the loss would be silent. The capture in Fixtures/weex-ws exercised plain <c>@depth</c>
    /// only, so "200 is accepted" is a vendor claim this repository has NOT verified; the builder's
    /// level gate is what makes believing it safe. If WEEX rejects the channel, or answers with a
    /// thinner <c>l</c>, no book is ever served and depth stays on REST — a visible loss of the WS
    /// path, not an invisible loss of two bands.
    /// </summary>
    private const string DepthChannel = "depth200";
    private const int DepthLevels = 200;

    /// <summary>Symbols per SUBSCRIBE frame. The envelope takes an array, but the capture only ever
    /// sent one element, so the venue's real ceiling is unknown — and guessing high is expensive
    /// here, since an oversized frame would come back as a reject and rejects are what close the
    /// socket. Conservative on purpose: 1000 symbols cost 20 frames once per connection.</summary>
    private const int SubscribeChunk = 50;

    private static readonly TimeSpan SubscriptionRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResyncDebounce = TimeSpan.FromSeconds(5);

    private readonly WsConnection _conn;
    private readonly WeexFuturesClient _client;
    private readonly VenueGate _gate;
    private readonly WeexBookBuilder _books;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _crosscheckInterval;
    private readonly int _driftBps;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastResync = new(StringComparer.Ordinal);

    /// <summary>Stored (v2) symbols we intend to be subscribed to.</summary>
    private volatile string[] _symbols = [];

    /// <summary>Wire (v3) symbol → stored (v2) symbol, replaced wholesale so readers never see a
    /// half-built map.</summary>
    private volatile Dictionary<string, string> _v3ToV2 = new(StringComparer.Ordinal);

    private long _nextRequestId;
    private long _gapsSinceLastReport;
    private int _thinLevelReported;
    private CancellationToken _ct;

    public WeexWsFeed(
        string wsUrl, WeexFuturesClient client, VenueGate gate, ILoggerFactory loggers, TimeProvider clock,
        TimeSpan staleAfter, TimeSpan crosscheckInterval, int driftBps)
    {
        _client = client;
        _gate = gate;
        _clock = clock;
        _log = loggers.CreateLogger("Weex.Ws");
        _conn = new WsConnection(wsUrl, loggers.CreateLogger("Weex.Ws.Conn"), clock);
        _books = new WeexBookBuilder(DepthLevels);
        _staleAfter = staleAfter;
        _crosscheckInterval = crosscheckInterval;
        _driftBps = driftBps;
    }

    /// <summary>Launches the feed in the background, tied to <paramref name="ct"/>.</summary>
    public void Start(CancellationToken ct)
    {
        _ct = ct;
        _ = RunAsync(ct);
    }

    /// <summary>Depth for one symbol from the live book, or false (caller falls back to REST). Gated
    /// on overall feed health — a dead socket must not serve a clean-but-frozen book as fresh; a
    /// quiet book under a live socket is served, and the cross-check below is what distinguishes the
    /// two.</summary>
    public bool TryGetDepth(string symbol, out Depth depth)
    {
        if (!Healthy)
        {
            depth = default!;
            return false;
        }

        return _books.TryGetDepth(symbol, _clock.GetUtcNow(), out depth);
    }

    /// <summary>
    /// Connected, with at least one clean full-depth book updated inside the staleness window.
    ///
    /// Deliberately weaker than Kraken's "half the symbols fresh", and the difference is a property
    /// of the two streams, not a relaxation of standards. Kraken counts its TICKER cache, and that
    /// feed pushes for every product on every tick, so half of them being stale really does mean
    /// something is wrong. WEEX offers no ticker channel at all (@bookTicker, @markPrice and
    /// @miniTicker are all rejected), so the only stream here is change-driven depth: on a venue
    /// with a thousand perpetuals, most of them quiet, "half the books unchanged in 30 s" is the
    /// normal state of a healthy socket. Requiring a majority would leave the feed permanently
    /// unhealthy, and a WS path that silently never serves is worse than none — it looks like it
    /// works.
    ///
    /// Correctness for an individual symbol is carried where it belongs instead: the builder refuses
    /// an unseeded, dirty or too-thin book, <see cref="OnOpenAsync"/> distrusts every book across a
    /// reconnect, and <see cref="CrosscheckAsync"/> compares each book's top against the venue's own
    /// batched ticker to catch one that has frozen behind a socket that still looks alive.
    /// </summary>
    private bool Healthy => _conn.Connected && _books.FreshCount(_staleAfter, _clock.GetUtcNow()) >= 1;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshSymbolsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "WEEX WS: initial symbol fetch failed; starting empty, will refresh");
        }

        await Task.WhenAll(
            _conn.RunAsync(OnOpenAsync, OnMessage, ct),
            LoopAsync(RefreshSymbolsAsync, SubscriptionRefresh, "subscription refresh", ct),
            LoopAsync(CrosscheckAsync, _crosscheckInterval, "cross-check", ct));
    }

    /// <summary>
    /// Runs on every connect, including every RECONNECT, and the first line is the load-bearing one.
    /// <see cref="WsConnection"/> comes back after ~1 s of backoff and reports Connected the moment
    /// the socket opens, while the builder still holds every book as it stood before the drop. Those
    /// books are stale by exactly as much as the outage lasted, the frames that would have revealed
    /// it were never delivered, and the health check above would serve them as live. So they are
    /// distrusted BEFORE the resubscribe that will reseed them.
    /// </summary>
    private async Task OnOpenAsync(CancellationToken ct)
    {
        _books.MarkAllDirty();

        var symbols = _symbols;
        _log.LogInformation("WEEX WS: subscribing {Count} symbols to @{Channel}", symbols.Length, DepthChannel);
        await SubscribeAsync("SUBSCRIBE", symbols, ct);
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // The subscribe/unsubscribe ack envelope. result:false is a rejected channel, answered on
            // the same envelope as a success — it is not a transport error, and it must not be
            // retried (see the class remarks: six rejects close the connection).
            if (root.TryGetProperty("result", out var result))
            {
                if (result.ValueKind == JsonValueKind.False)
                {
                    var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                    _log.LogWarning("WEEX WS rejected a request: {Msg}. Not retrying — repeated rejects close the socket", msg);
                }

                return;
            }

            // The unprompted 'connected' greeting and the server's 60 s application-level ping.
            // Neither needs an answer: over a four-minute idle none were answered and the server did
            // not close (captured in Fixtures/weex-ws/ping-pong.txt). Frames keep the idle watchdog
            // fed on their own.
            if (root.TryGetProperty("event", out _))
            {
                return;
            }

            if (!root.TryGetProperty("e", out var kind))
            {
                return;
            }

            switch (kind.GetString())
            {
                case "depthSnapshot":
                    HandleDepth(root, isSnapshot: true);
                    break;
                case "depth":
                    HandleDepth(root, isSnapshot: false);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads a depth frame field by field off the <see cref="JsonElement"/> rather than binding it to
    /// a record, and that is not laziness. These frames carry <c>U</c> and <c>u</c> as distinct
    /// fields; a record declaring both with <c>JsonPropertyName</c> throws
    /// <c>InvalidOperationException</c> on .NET 10 under case-insensitive binding, which is exactly
    /// what <c>JsonSerializerDefaults.Web</c> — used by every other DTO in this connector — turns on.
    /// Two fields whose names differ only in case cannot be bound that way at all, so they are read
    /// by hand, case-sensitively, and the confusion never gets a chance to start.
    /// </summary>
    private void HandleDepth(JsonElement root, bool isSnapshot)
    {
        if (!root.TryGetProperty("s", out var symbolEl) || symbolEl.GetString() is not { } wireSymbol)
        {
            return;
        }

        // A frame for a symbol we no longer track (unsubscribed a moment ago, or never mapped) is
        // dropped rather than guessed at: the v2 spelling is not derivable from the v3 one.
        if (!_v3ToV2.TryGetValue(wireSymbol, out var symbol))
        {
            return;
        }

        if (!root.TryGetProperty("U", out var firstEl) || !root.TryGetProperty("u", out var lastEl))
        {
            return;
        }

        var levels = root.TryGetProperty("l", out var levelEl) && levelEl.TryGetInt32(out var l) ? l : 0;
        var bids = Levels(root, "b");
        var asks = Levels(root, "a");

        // Our receive time, not the venue's E — the builder reads it back as this feed's freshness
        // signal, and that has to measure our own receipt.
        var at = _clock.GetUtcNow();

        if (isSnapshot)
        {
            _books.ApplySnapshot(symbol, lastEl.GetInt64(), levels, bids, asks, at);

            // Loud, because a thinner book than we asked for means the WS depth path is inert: the
            // books sequence correctly and are never served. Silence here would look exactly like a
            // healthy feed that simply has no data. Once per process, though — this would otherwise
            // fire on every one of a thousand snapshots on every connect.
            if (levels < DepthLevels && Interlocked.Exchange(ref _thinLevelReported, 1) == 0)
            {
                _log.LogWarning(
                    "WEEX WS: asked for @{Channel} but {Symbol} arrives with l={Levels}; depth stays on REST",
                    DepthChannel, symbol, levels);
            }

            return;
        }

        if (_books.ApplyDelta(symbol, firstEl.GetInt64(), lastEl.GetInt64(), levels, bids, asks, at)
            == WeexBookBuilder.DeltaResult.Gap)
        {
            // Per-symbol at Debug, not Warning: a thousand symbols make a per-gap warning a wall of
            // text that hides the thing worth seeing, which is the RATE. The refresh loop reports the
            // count instead.
            Interlocked.Increment(ref _gapsSinceLastReport);
            _log.LogDebug("WEEX WS: sequence break on {Symbol}; book dirty, resyncing", symbol);
            _ = ResyncBookAsync(symbol);
        }
    }

    /// <summary>Levels are <c>[price, qty]</c> pairs of STRINGS, both of them — asserted against the
    /// capture, not assumed. A malformed pair drops the whole frame's side rather than half-applying
    /// it: a partially applied delta is a book that is wrong without being dirty.</summary>
    private static List<(double Price, double Qty)> Levels(JsonElement root, string side)
    {
        if (!root.TryGetProperty(side, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<(double, double)>(array.GetArrayLength());
        foreach (var level in array.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Array || level.GetArrayLength() < 2
                || !double.TryParse(level[0].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                || !double.TryParse(level[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var qty))
            {
                return [];
            }

            list.Add((price, qty));
        }

        return list;
    }

    /// <summary>
    /// Compares each live book's top against the venue's own batched ticker. This is what catches a
    /// book that has frozen behind a socket that still looks alive — a channel the venue quietly
    /// stopped serving looks identical to an illiquid symbol until something outside the socket
    /// disagrees with it. One batched call for the whole venue, through the gate, so the guard costs
    /// the same whether we watch 10 symbols or 1000.
    /// </summary>
    private async Task CrosscheckAsync(CancellationToken ct)
    {
        var tickers = await GatedAsync(_client.GetTickersAsync, ct);
        var drifted = 0;
        foreach (var t in tickers)
        {
            if (!WeexMarkets.IsLive(t))
            {
                continue;
            }

            var bid = WeexMarkets.Parse(t.BestBid);
            var ask = WeexMarkets.Parse(t.BestAsk);
            if (bid <= 0 || ask <= 0)
            {
                continue;
            }

            var restMid = (bid + ask) / 2;
            if (!_books.TryGetTopMid(t.Symbol, out var wsMid) || wsMid <= 0)
            {
                continue;
            }

            var driftBps = Math.Abs(restMid - wsMid) / restMid * 10_000.0;
            if (driftBps > _driftBps)
            {
                drifted++;
                _books.MarkDirty(t.Symbol);
                _ = ResyncBookAsync(t.Symbol);
            }
        }

        if (drifted > 0)
        {
            _log.LogWarning("WEEX WS cross-check: {Count} books drifted past {Bps} bps; resyncing", drifted, _driftBps);
        }
    }

    /// <summary>Unsubscribe then subscribe one symbol, which is the captured way to get a fresh
    /// snapshot: the unsubscribe provably stops the stream and the subscribe provably starts it with
    /// a snapshot. Debounced, because a drifting symbol will keep drifting until the new snapshot
    /// lands.</summary>
    private async Task ResyncBookAsync(string symbol)
    {
        var now = _clock.GetUtcNow();
        if (_lastResync.TryGetValue(symbol, out var last) && now - last < ResyncDebounce)
        {
            return;
        }

        _lastResync[symbol] = now;
        await SubscribeAsync("UNSUBSCRIBE", [symbol], _ct);
        await SubscribeAsync("SUBSCRIBE", [symbol], _ct);
    }

    /// <summary>
    /// Rebuilds the symbol set from the venue's batched ticker — the same "has a real market" rule
    /// the REST adapter applies, so the socket never carries a channel discovery has already written
    /// off, and diffs the subscriptions. The v3→v2 map is rebuilt here too: it is the venue's own
    /// list, not a guessed inverse of <see cref="WeexMarkets.ToV3Symbol"/>.
    /// </summary>
    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        var tickers = await GatedAsync(_client.GetTickersAsync, ct);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tickers)
        {
            if (WeexMarkets.IsLive(t))
            {
                // TryAdd, not [], because two v2 spellings can collapse onto one v3 symbol; the
                // first wins deterministically rather than the loop throwing or the last silently
                // stealing the stream.
                map.TryAdd(WeexMarkets.ToV3Symbol(t.Symbol), t.Symbol);
            }
        }

        var next = map.Values.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var prev = _symbols;
        _v3ToV2 = map;
        _symbols = next;

        var gaps = Interlocked.Exchange(ref _gapsSinceLastReport, 0);
        _log.LogInformation(
            "WEEX WS: {Fresh} of {Total} books fresh, {Gaps} sequence breaks since the last report",
            _books.FreshCount(_staleAfter, _clock.GetUtcNow()), next.Length, gaps);

        if (!_conn.Connected)
        {
            return;   // OnOpen will subscribe the whole set on connect
        }

        var added = next.Except(prev, StringComparer.Ordinal).ToArray();
        var removed = prev.Except(next, StringComparer.Ordinal).ToArray();
        if (added.Length > 0)
        {
            await SubscribeAsync("SUBSCRIBE", added, ct);
        }

        if (removed.Length > 0)
        {
            await SubscribeAsync("UNSUBSCRIBE", removed, ct);
            foreach (var symbol in removed)
            {
                _books.Remove(symbol);
            }
        }
    }

    /// <summary>Binance-shaped envelope: <c>{"method":"SUBSCRIBE","params":["btcusdt@depth200"],"id":N}</c>.
    /// The id is echoed on the ack; it is monotonic here only so a rejected request can be told from
    /// its neighbours in a log.</summary>
    private async Task SubscribeAsync(string method, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        for (var i = 0; i < symbols.Count; i += SubscribeChunk)
        {
            var chunk = symbols.Skip(i).Take(SubscribeChunk);
            var parameters = new StringBuilder();
            foreach (var symbol in chunk)
            {
                if (parameters.Length > 0)
                {
                    parameters.Append(',');
                }

                parameters.Append('"').Append(WeexMarkets.ToV3Symbol(symbol)).Append('@').Append(DepthChannel).Append('"');
            }

            if (parameters.Length == 0)
            {
                continue;
            }

            var id = Interlocked.Increment(ref _nextRequestId);
            await _conn.SendAsync($"{{\"method\":\"{method}\",\"params\":[{parameters}],\"id\":{id}}}", ct);
        }
    }

    /// <summary>Every REST call this feed makes goes through the venue's shared ceiling — the same
    /// one the depth sweep and the open-interest cycle contend for. The ceiling is per venue, not per
    /// caller, which is the entire reason <see cref="VenueGate"/> exists.</summary>
    private async Task<T> GatedAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using (await _gate.AcquireAsync(ct))
        {
            return await call(ct);
        }
    }

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
                // A 429 is the venue speaking about the whole IP, not about this loop, so it goes to
                // the gate and slows every caller rather than only this one backing off.
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                {
                    _gate.Penalize();
                }

                _log.LogWarning(ex, "WEEX WS {What} pass failed", what);
            }
        }
    }
}

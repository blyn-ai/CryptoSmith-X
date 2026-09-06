using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// The live Binance USDⓈ-M order book over WebSocket
/// (<c>wss://fstream.binance.com/public/stream</c>): subscribes <c>@depth@100ms</c> for every in-scope
/// perpetual, seeds each book from a REST snapshot, maintains them from the diff stream, and serves
/// the adapter depth only while the feed is genuinely healthy. Same shape as
/// <see cref="Kraken.KrakenWsFeed"/> and <see cref="Weex.WeexWsFeed"/> — a <see cref="WsConnection"/>,
/// a builder, a subscription-refresh loop and a REST cross-check, all driven by the supervisor's
/// token — with the three Binance-specific parts called out below rather than smuggled in.
///
/// WHY THIS EXISTS, and it is not the reason it was on the other two venues. There, the socket
/// replaced a REST sweep that could not keep up. Here the sweep keeps up fine; the problem is that
/// its ANSWER IS EMPTY. Binance's REST book returns a window of levels, and at a 0.10 tick the 100
/// levels this adapter can afford span 1.4 bps of BTCUSDT — so <see cref="Kraken.DepthMath"/> nulls
/// all three bands, and even the venue's deepest available window (limit=1000, weight 20 per symbol
/// per call) spans only 17 bps and still cannot bound 25. The socket does not make depth cheaper. It
/// makes depth EXIST on the venue's most important symbols, because a maintained book keeps every
/// level the venue publishes instead of a window of them.
///
/// THREE THINGS THAT ARE NOT WEEX'S OR KRAKEN'S:
///
///   1. THE ROUTED PATH, and a failure mode with no error attached to it. Binance split its public
///      socket across <c>/public</c>, <c>/market</c> and <c>/private</c>, and a stream asked for on
///      the wrong path answers HTTP 101, ACKNOWLEDGES the SUBSCRIBE with <c>{"result":null}</c> —
///      the same success envelope a correct subscribe gets — and then sends nothing at all, forever.
///      Captured both ways in Fixtures/binance-ws/session-transcript.txt. Neither the handshake nor
///      the ack can tell you; only the absence of frames can, which is why
///      <see cref="StartupLiveness"/> exists and is the only detector this venue permits.
///   2. THE SEAM. Binance does not put the seeding snapshot on the socket, so the book is assembled
///      across a REST fetch and the frames that arrive during it. That is a race, it has a documented
///      procedure, and the FIRST frame after a seed is validated by a different rule than every frame
///      after that — see <see cref="BinanceBookBuilder"/>, which is where both rules live.
///   3. THE SEED IS EXPENSIVE AND THE BUDGET IS METERED. One seed is weight 20 of a 2400/minute IP
///      budget, and a reconnect needs one per symbol. Seeding is therefore a slow background trickle
///      (<see cref="SeedPace"/>) through the venue gate rather than a burst on connect: a full
///      ~570-symbol reseed spends ~600 weight/minute for ~19 minutes, during which depth for the
///      not-yet-seeded symbols simply comes from REST as it did before. A burst would have been ~11
///      000 weight — four and a half minutes of the venue's entire budget spent at once, which is a
///      429 and an outage on every other collector sharing this IP.
/// </summary>
public sealed class BinanceWsFeed : IBinanceLiveFeed
{
    /// <summary>
    /// The stream this feed subscribes, and the routed path it lives on. The map has one entry
    /// because one dataset comes over the socket (see <see cref="IBinanceLiveFeed"/> for why the
    /// snapshot deliberately stays on REST) — but the PATH is written down next to the stream rather
    /// than assumed from the connection, because on this venue a stream on the wrong path fails
    /// silently and permanently. The next stream added here has to state its path too.
    ///
    ///     /public   @depth, @bookTicker, @aggTrade, @kline, !bookTicker
    ///     /market   !markPrice@arr, !ticker@arr, !miniTicker@arr
    ///
    /// Verified live in both directions: <c>btcusdt@depth@100ms</c> delivered 76 frames in 8 s on
    /// /public and 0 frames in 12 s on /market; <c>!markPrice@arr@1s</c> did the reverse.
    /// </summary>
    private const string DepthStreamSuffix = "@depth@100ms";

    /// <summary>Streams per SUBSCRIBE frame. Verified live: one connection to <c>/public/stream</c>
    /// took all 566 in-scope symbols in six frames of 100 and delivered 22 302 frames across all 566
    /// in twelve seconds. Binance caps INCOMING messages at ten per second per connection, so the
    /// frames are spaced by <see cref="SubscribePause"/> rather than sent in one burst.</summary>
    private const int SubscribeChunk = 100;

    private static readonly TimeSpan SubscribePause = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SubscriptionRefresh = TimeSpan.FromMinutes(5);

    /// <summary>How long after subscribing a silent socket is reported. Generous on purpose: this is
    /// not a health threshold — the idle watchdog in <see cref="WsConnection"/> already tears down a
    /// socket that has gone quiet — it is the ONE detector for a misrouted stream, whose signature is
    /// a perfectly healthy-looking connection that will never deliver a single frame. Fifteen seconds
    /// of silence on a venue that delivers ~1800 frames a second is not ambiguous.</summary>
    private static readonly TimeSpan StartupLiveness = TimeSpan.FromSeconds(15);

    /// <summary>Seconds between REST seeds. See the class remarks for the arithmetic; the short
    /// version is that a seed costs weight 20 and a reconnect wants ~570 of them.</summary>
    private static readonly TimeSpan SeedPace = TimeSpan.FromSeconds(2);

    private readonly WsConnection _conn;
    private readonly BinanceUsdmClient _client;
    private readonly VenueGate _gate;
    private readonly BinanceBookBuilder _books;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _crosscheckInterval;
    private readonly int _driftBps;

    /// <summary>Symbols we intend to be subscribed to. Binance has one symbol spelling, so unlike
    /// WEEX there is no map to keep — only a case fold, in <see cref="BinanceMarkets.ToStream"/>.</summary>
    private volatile string[] _symbols = [];

    private long _nextRequestId;
    private long _lastFrameTicks;
    private long _framesThisConnection;
    private long _gapsSinceLastReport;
    private long _seedsSinceLastReport;

    public BinanceWsFeed(
        string wsUrl, BinanceUsdmClient client, VenueGate gate, ILoggerFactory loggers, TimeProvider clock,
        TimeSpan staleAfter, TimeSpan crosscheckInterval, int driftBps)
    {
        _client = client;
        _gate = gate;
        _clock = clock;
        _log = loggers.CreateLogger("Binance.Ws");
        _conn = new WsConnection(wsUrl, loggers.CreateLogger("Binance.Ws.Conn"), clock);
        _books = new BinanceBookBuilder();
        _staleAfter = staleAfter;
        _crosscheckInterval = crosscheckInterval;
        _driftBps = driftBps;
    }

    /// <summary>Launches the feed in the background, tied to <paramref name="ct"/>.</summary>
    public void Start(CancellationToken ct) => _ = RunAsync(ct);

    /// <summary>Depth for one symbol from the live book, or false (caller falls back to REST). Gated
    /// on overall feed health — a dead socket must not serve a clean-but-frozen book as fresh; a
    /// quiet book under a live socket is served, and the cross-check is what distinguishes the two.
    /// Bands the seed never covered come back null inside a Depth that is otherwise real; see
    /// <see cref="BinanceBookBuilder.TryGetDepth"/>.</summary>
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
    /// Connected, receiving, and holding at least one clean book updated inside the staleness window.
    ///
    /// The middle condition is the one that is not in the other feeds, and it is what a silently
    /// misrouted socket makes necessary. Books here are seeded from REST, which keeps working
    /// perfectly well when the socket delivers nothing: a feed whose health read only "connected"
    /// and "some clean book exists" would report itself healthy on a connection that has never
    /// carried a single frame, and serve seed-time books as live depth for as long as the process
    /// ran. On a venue that pushes ~1800 frames a second, "no frame in <c>ws_stale_after_s</c>"
    /// cannot happen to a working feed.
    /// </summary>
    private bool Healthy =>
        _conn.Connected
        && _clock.GetUtcNow() - LastFrameAt <= _staleAfter
        && _books.FreshCount(_staleAfter, _clock.GetUtcNow()) >= 1;

    private DateTimeOffset LastFrameAt => new(Interlocked.Read(ref _lastFrameTicks), TimeSpan.Zero);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshSymbolsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Binance WS: initial symbol fetch failed; starting empty, will refresh");
        }

        await Task.WhenAll(
            _conn.RunAsync(OnOpenAsync, OnMessage, ct),
            SeedLoopAsync(ct),
            LoopAsync(RefreshSymbolsAsync, SubscriptionRefresh, "subscription refresh", ct),
            LoopAsync(CrosscheckAsync, _crosscheckInterval, "cross-check", ct));
    }

    /// <summary>
    /// Runs on every connect, including every RECONNECT, and the first line is the load-bearing one.
    /// <see cref="WsConnection"/> comes back after ~1 s of backoff and reports Connected the moment
    /// the socket opens, while the builder still holds every book as it stood before the drop. Those
    /// books are stale by exactly as much as the outage lasted, the frames that would have revealed
    /// it were never delivered, and the health check would serve them as live. So they are distrusted
    /// BEFORE the resubscribe that will reseed them — which is also what puts every symbol back in
    /// front of the seed loop, since "dirty" is the only thing that loop looks at.
    /// </summary>
    private async Task OnOpenAsync(CancellationToken ct)
    {
        _books.MarkAllDirty();
        Interlocked.Exchange(ref _framesThisConnection, 0);
        Interlocked.Exchange(ref _lastFrameTicks, _clock.GetUtcNow().UtcTicks);

        var symbols = _symbols;
        _log.LogInformation("Binance WS: subscribing {Count} symbols to {Stream}", symbols.Length, DepthStreamSuffix);
        await SubscribeAsync("SUBSCRIBE", symbols, ct);

        // Fire-and-forget on purpose: WsConnection awaits this method BEFORE it starts reading, so
        // waiting for the liveness window here would guarantee the answer "no frames" and would also
        // stop any from arriving. The check has to run alongside the receive loop, not in front of it.
        _ = WatchStartupLivenessAsync(symbols.Length, ct);
    }

    /// <summary>
    /// The only way to notice that a stream was asked for on the wrong routed path. Binance answers
    /// such a subscribe with HTTP 101 and <c>{"result":null}</c> — indistinguishable from success —
    /// and then never sends a frame. Captured both ways: on /public the depth stream delivered 76
    /// frames in 8 s; on /market the identical subscribe, identically acknowledged, delivered 0 in 12.
    ///
    /// This reports rather than acts, and that division is deliberate. Reconnecting cannot fix a
    /// wrong path, and <see cref="WsConnection"/>'s idle watchdog already handles the case where a
    /// once-working socket goes quiet. What is missing without this is any signal at all: the feed
    /// would sit connected and silent, the books would be seeded from REST and look clean, and the
    /// only visible symptom would be depth that never got better.
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

        if (Interlocked.Read(ref _framesThisConnection) > 0 || subscribed == 0)
        {
            return;
        }

        _log.LogError(
            "Binance WS: subscribed {Count} symbols to {Stream} and received NOTHING in {Seconds}s. The "
            + "handshake and the subscribe ack both succeed on a misrouted stream — this venue splits "
            + "its public socket across /public, /market and /private, and a stream on the wrong path "
            + "is acknowledged and then silent forever. Check that the segment's ws_url is the "
            + "/public endpoint. Depth stays on REST until frames arrive.",
            subscribed, DepthStreamSuffix, StartupLiveness.TotalSeconds);
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

            // The SUBSCRIBE/UNSUBSCRIBE ack. Worth noting what it does NOT tell us: a subscribe to a
            // stream that this path does not serve is answered with exactly this envelope and
            // result:null, so a successful ack is not evidence of anything. See
            // WatchStartupLivenessAsync.
            if (root.TryGetProperty("result", out _))
            {
                return;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Counted before the payload is understood: liveness is about the SOCKET, and a frame we
            // could not parse still proves the connection is carrying traffic.
            Interlocked.Increment(ref _framesThisConnection);
            Interlocked.Exchange(ref _lastFrameTicks, _clock.GetUtcNow().UtcTicks);

            if (!data.TryGetProperty("e", out var kind) || kind.GetString() != "depthUpdate")
            {
                return;
            }

            HandleDepth(data);
        }
    }

    private void HandleDepth(JsonElement data)
    {
        var frame = data.Deserialize<BinanceWsDepth>(BinanceJson.Options);
        if (frame is null || frame.Symbol.Length == 0)
        {
            return;
        }

        // A frame whose levels do not parse must DIRTY the book, not lose a side quietly. Applying
        // the half that parsed and then chaining past it produces a book that is wrong without being
        // dirty — the one state nothing downstream can detect, since the sequence still runs clean
        // and the cross-check only looks at the top of book.
        if (!TryLevels(frame.Bids, out var bids) || !TryLevels(frame.Asks, out var asks))
        {
            _log.LogWarning(
                "Binance WS: unreadable level in a depthUpdate for {Symbol}; book marked dirty and reseeded "
                + "rather than half-applied", frame.Symbol);
            _books.MarkDirty(frame.Symbol);
            return;
        }

        // Our receive time, not the venue's E — the builder reads it back as this feed's freshness
        // signal, and that has to measure our own receipt.
        var result = _books.ApplyDelta(
            frame.Symbol, frame.FirstUpdateId, frame.LastUpdateId, frame.PreviousUpdateId,
            bids, asks, _clock.GetUtcNow());

        if (result == BinanceBookBuilder.DeltaResult.Gap)
        {
            // Per-symbol at Debug, not Warning: ~570 symbols make a per-gap warning a wall of text
            // that hides the thing worth seeing, which is the RATE. The refresh loop reports the
            // count. The book is already dirty, which is all the seed loop needs.
            Interlocked.Increment(ref _gapsSinceLastReport);
            _log.LogDebug("Binance WS: sequence break on {Symbol}; book dirty, will reseed", frame.Symbol);
        }
    }

    /// <summary>
    /// Seeds every book that needs one, slowly and through the venue gate.
    ///
    /// "Needs one" is deliberately the only input: a fresh connection, a sequence gap, a book that
    /// drifted past the cross-check and a book whose levels would not parse all end in the same
    /// state — dirty — so they all get the same treatment without four code paths deciding
    /// separately what to do about it. The walk is continuous rather than event-driven for the same
    /// reason: nothing can be forgotten by a queue that was full or a signal that was missed.
    /// </summary>
    private async Task SeedLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var seeded = 0;
            foreach (var symbol in _symbols)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                // Seeding a book the socket is not feeding would produce a snapshot that never gets
                // an update and then ages out — REST work whose only product is a book that looks
                // clean. Wait for the socket instead.
                if (!_conn.Connected || !_books.NeedsSeed(symbol))
                {
                    continue;
                }

                try
                {
                    BinanceDepth snapshot;
                    using (await _gate.AcquireAsync(ct))
                    {
                        snapshot = await _client.GetDepthAsync(symbol, BinanceUsdmClient.SeedDepthLimit, ct);
                    }

                    // Deliberately not `continue`: the weight is already spent by the time a
                    // snapshot turns out to be unusable, and `continue` would jump past the
                    // SeedPace delay at the bottom of this loop. The symbols that fail are the same
                    // ones every pass — a venue-side depth incident, or a batch of thin perps — so
                    // skipping the pace on failure is skipping it on precisely the symbols that
                    // repeat, and the trickle this loop exists to be becomes the burst it exists to
                    // avoid: 570 symbols at weight 20 in the time the gate allows is ~16,700
                    // weight/min against a budget of 2,400.
                    if (TrySeedLevels(snapshot, out var bids, out var asks)
                        // Our receive time, for the same reason the deltas use it.
                        && _books.ApplySnapshot(symbol, snapshot.LastUpdateId, bids, asks, _clock.GetUtcNow()))
                    {
                        seeded++;
                        Interlocked.Increment(ref _seedsSinceLastReport);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One symbol's failure must not stall the walk; the book stays dirty and comes
                    // round again. A 429 is the venue speaking about the whole IP, so it goes to the
                    // gate and slows every caller rather than only this loop.
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                    {
                        _gate.Penalize();
                    }

                    _log.LogDebug(ex, "Binance WS: seeding {Symbol} failed; will retry", symbol);
                }

                try
                {
                    await Task.Delay(SeedPace, _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (seeded == 0)
            {
                // Nothing wanted seeding this time round; do not spin the symbol list.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Compares each live book's top against the venue's own batched book ticker. This is what
    /// catches a book that has frozen behind a socket that still looks alive — one batched call for
    /// the whole venue (weight 5), through the gate, so the guard costs the same whether we watch ten
    /// symbols or a thousand.
    ///
    /// It also asks a second question no other venue here needs: has the price walked out of the
    /// window the seed covered? If it has, the deep bands have gone dark and the book wants a fresh
    /// snapshot even though its sequence is perfect. Both answers are expressed the same way — mark
    /// it dirty, and the seed loop picks it up — so a symbol cannot be queued twice or forgotten once.
    /// </summary>
    private async Task CrosscheckAsync(CancellationToken ct)
    {
        IReadOnlyList<BinanceBookTicker> tickers;
        using (await _gate.AcquireAsync(ct))
        {
            tickers = await _client.GetBookTickersAsync(ct);
        }

        var drifted = 0;
        var outgrown = 0;
        foreach (var t in tickers)
        {
            var bid = Parse(t.BidPrice);
            var ask = Parse(t.AskPrice);
            if (bid <= 0 || ask <= 0)
            {
                continue;
            }

            if (!_books.TryGetTopMid(t.Symbol, out var wsMid) || wsMid <= 0)
            {
                continue;
            }

            var restMid = (bid + ask) / 2;
            if (Math.Abs(restMid - wsMid) / restMid * 10_000.0 > _driftBps)
            {
                drifted++;
                _books.MarkDirty(t.Symbol);
            }
            else if (_books.SeedWindowOutgrown(t.Symbol))
            {
                outgrown++;
                _books.MarkDirty(t.Symbol);
            }
        }

        if (drifted > 0 || outgrown > 0)
        {
            _log.LogInformation(
                "Binance WS cross-check: {Drifted} books drifted past {Bps} bps, {Outgrown} outgrew the window "
                + "their snapshot covered; both reseeding", drifted, _driftBps, outgrown);
        }
    }

    /// <summary>
    /// Rebuilds the symbol set from the venue's own listing (weight 1) using the SAME scope rule
    /// discovery applies, so the socket never carries a channel discovery has already written off,
    /// and diffs the subscriptions. Also the feed's periodic report: what it says out loud is the
    /// RATE of gaps and seeds, because on ~570 symbols the individual events are noise and the rate
    /// is the signal.
    /// </summary>
    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        IReadOnlyList<BinanceSymbol> symbols;
        using (await _gate.AcquireAsync(ct))
        {
            symbols = await _client.GetSymbolsAsync(ct);
        }

        var next = symbols
            .Where(s => BinanceMarkets.IsInScope(s) && s.Status == "TRADING")
            .Select(s => s.Symbol)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var prev = _symbols;
        _symbols = next;

        _log.LogInformation(
            "Binance WS: {Fresh} of {Total} books live, {Seeds} seeded and {Gaps} sequence breaks since the "
            + "last report",
            _books.FreshCount(_staleAfter, _clock.GetUtcNow()), next.Length,
            Interlocked.Exchange(ref _seedsSinceLastReport, 0),
            Interlocked.Exchange(ref _gapsSinceLastReport, 0));

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

    /// <summary><c>{"method":"SUBSCRIBE","params":["btcusdt@depth@100ms"],"id":N}</c>. The id is echoed
    /// on the ack; it is monotonic here only so one request can be told from its neighbours in a log,
    /// since the ack itself carries no other identity — and, on this venue, no information.</summary>
    private async Task SubscribeAsync(string method, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        for (var i = 0; i < symbols.Count; i += SubscribeChunk)
        {
            var parameters = new StringBuilder();
            foreach (var symbol in symbols.Skip(i).Take(SubscribeChunk))
            {
                if (parameters.Length > 0)
                {
                    parameters.Append(',');
                }

                parameters.Append('"').Append(BinanceMarkets.ToStream(symbol)).Append(DepthStreamSuffix).Append('"');
            }

            if (parameters.Length == 0)
            {
                continue;
            }

            var id = Interlocked.Increment(ref _nextRequestId);
            await _conn.SendAsync($"{{\"method\":\"{method}\",\"params\":[{parameters}],\"id\":{id}}}", ct);

            // Binance caps incoming messages at ten per second per connection, and a connection that
            // breaks that rule is closed rather than throttled.
            if (i + SubscribeChunk < symbols.Count)
            {
                await Task.Delay(SubscribePause, _clock, ct);
            }
        }
    }

    /// <summary>Levels are <c>[price, qty]</c> pairs of STRINGS, both of them — asserted against the
    /// capture, not assumed. Returns false for a pair that does not read, and the CALLER dirties the
    /// book: this method must not be able to answer "nothing changed on that side", because that is
    /// indistinguishable from a real empty side and is how a half-applied frame becomes a book that
    /// is wrong without being dirty.</summary>
    /// <summary>
    /// Both sides of a seed snapshot, or false. False means the book stays dirty and comes round
    /// again — never that the levels are used as far as they go.
    ///
    /// A one-sided snapshot would seed a book with no floor or no ceiling, and the window guard
    /// reads a missing side as "nothing is covered": permanently clean, permanently useless, and
    /// never asking to be seeded again because by its own account nothing is wrong with it.
    /// </summary>
    private static bool TrySeedLevels(
        BinanceDepth snapshot,
        out List<(double Price, double Qty)> bids,
        out List<(double Price, double Qty)> asks)
    {
        asks = [];
        return TryLevels(snapshot.Bids, out bids) && TryLevels(snapshot.Asks, out asks)
            && bids.Count > 0 && asks.Count > 0;
    }

    private static bool TryLevels(List<string[]>? array, out List<(double Price, double Qty)> levels)
    {
        levels = [];
        if (array is null)
        {
            return true;   // the venue omits a side that changed nothing; that is genuinely empty
        }

        levels = new List<(double, double)>(array.Count);
        foreach (var level in array)
        {
            if (level.Length < 2
                || !double.TryParse(level[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                || !double.TryParse(level[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var qty))
            {
                return false;
            }

            levels.Add((price, qty));
        }

        return true;
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);

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

                _log.LogWarning(ex, "Binance WS {What} pass failed", what);
            }
        }
    }
}

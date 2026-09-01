using System.Collections.Concurrent;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

/// <summary>
/// The live Kraken Futures market over WebSocket: subscribes <c>ticker</c> and <c>book</c> for every
/// PF_ perpetual, keeps a fresh ticker cache and a maintained order book, and serves the adapter a
/// slice only while it is genuinely healthy. Everything is driven by the supervisor's cancellation
/// token — the feed starts when the adapter is built and stops when the exchange is disabled. A REST
/// cross-check catches a book that has silently frozen behind a live socket.
/// </summary>
public sealed class KrakenWsFeed : IKrakenLiveFeed
{
    private const string PerpPrefix = "PF_";
    private static readonly TimeSpan SubscriptionRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResyncDebounce = TimeSpan.FromSeconds(5);

    private readonly WsConnection _conn;
    private readonly KrakenFuturesClient _client;
    private readonly MarketCache<Ticker> _tickers;
    private readonly KrakenBookBuilder _books;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _crosscheckInterval;
    private readonly int _driftBps;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastResync = new(StringComparer.Ordinal);

    private volatile string[] _symbols = [];
    private CancellationToken _ct;

    public KrakenWsFeed(
        string wsUrl, KrakenFuturesClient client, ILoggerFactory loggers, TimeProvider clock,
        TimeSpan staleAfter, TimeSpan crosscheckInterval, int driftBps)
    {
        _client = client;
        _clock = clock;
        _log = loggers.CreateLogger("Kraken.Ws");
        _conn = new WsConnection(wsUrl, loggers.CreateLogger("Kraken.Ws.Conn"), clock);
        _tickers = new MarketCache<Ticker>(clock);
        _books = new KrakenBookBuilder();
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

    /// <summary>A fresh slice from the cache with depth attached, or false when the feed is unhealthy
    /// (caller falls back to REST). Individually-stale symbols are simply omitted — their snapshot row
    /// then ages and the existing staleness logic sees it.</summary>
    public bool TryGetFreshTickers(out IReadOnlyList<Ticker> tickers)
    {
        if (!Healthy)
        {
            tickers = [];
            return false;
        }

        var now = _clock.GetUtcNow();
        var fresh = _tickers.FresherThan(_staleAfter);
        var result = new List<Ticker>(fresh.Count);
        foreach (var t in fresh)
        {
            var depth = _books.TryGetDepth(t.ExchangeSymbol, now, out var d) ? d : null;
            result.Add(t with { Depth = depth });
        }

        tickers = result;
        return true;
    }

    /// <summary>Depth for one symbol from the live book, or false (caller falls back to REST). Gated on
    /// overall feed health — a dead socket must not serve a clean-but-frozen book as fresh; a quiet
    /// book under a live socket is served.</summary>
    public bool TryGetDepth(string symbol, out Depth depth)
    {
        if (!Healthy)
        {
            depth = default!;
            return false;
        }

        return _books.TryGetDepth(symbol, _clock.GetUtcNow(), out depth);
    }

    private bool Healthy =>
        _conn.Connected && _tickers.FreshCount(_staleAfter) >= Math.Max(1, _symbols.Length / 2);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshSymbolsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Kraken WS: initial instrument fetch failed; starting empty, will refresh");
        }

        await Task.WhenAll(
            _conn.RunAsync(OnOpenAsync, OnMessage, ct),
            LoopAsync(RefreshSymbolsAsync, SubscriptionRefresh, "subscription refresh", ct),
            LoopAsync(CrosscheckAsync, _crosscheckInterval, "cross-check", ct));
    }

    private async Task OnOpenAsync(CancellationToken ct)
    {
        _log.LogInformation("Kraken WS: subscribing {Count} symbols", _symbols.Length);
        await SendFeedsAsync("subscribe", _symbols, ct);
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
            if (root.TryGetProperty("event", out var ev))
            {
                var name = ev.GetString();
                if (name is "error" or "alert")
                {
                    _log.LogWarning("Kraken WS event {Event}: {Body}", name, text);
                }

                return;
            }

            if (!root.TryGetProperty("feed", out var feedEl))
            {
                return;
            }

            switch (feedEl.GetString())
            {
                case "ticker":
                    HandleTicker(root);
                    break;
                case "book_snapshot":
                    HandleSnapshot(root);
                    break;
                case "book":
                    HandleDelta(root);
                    break;
            }
        }
    }

    private void HandleTicker(JsonElement root)
    {
        var t = root.Deserialize<KrakenWsTicker>(KrakenJson.Options);
        if (t is null || string.IsNullOrEmpty(t.ProductId))
        {
            return;
        }

        var at = t.Time > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t.Time) : _clock.GetUtcNow();
        _tickers.Set(t.ProductId, new Ticker(
            ExchangeSymbol: t.ProductId,
            ReceivedAt: at,
            LastPrice: t.Last,
            BidPrice: t.Bid,
            AskPrice: t.Ask,
            BidSize: t.BidSize,
            AskSize: t.AskSize,
            MarkPrice: t.MarkPrice,
            IndexPrice: t.Index,
            FundingRate: t.RelativeFundingRate,
            Turnover24h: t.VolumeQuote,
            OpenInterest: t.OpenInterest,
            OpenInterestAt: at,
            Depth: null));
    }

    private void HandleSnapshot(JsonElement root)
    {
        var s = root.Deserialize<KrakenWsBookSnapshot>(KrakenJson.Options);
        if (s is null || string.IsNullOrEmpty(s.ProductId))
        {
            return;
        }

        var bids = s.Bids.ConvertAll(l => (l.Price, l.Qty));
        var asks = s.Asks.ConvertAll(l => (l.Price, l.Qty));
        var at = s.Timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(s.Timestamp) : _clock.GetUtcNow();
        _books.ApplySnapshot(s.ProductId, s.Seq, bids, asks, at);
    }

    private void HandleDelta(JsonElement root)
    {
        var symbol = root.GetProperty("product_id").GetString();
        if (symbol is null)
        {
            return;
        }

        var isBid = root.GetProperty("side").GetString() == "buy";
        var seq = root.GetProperty("seq").GetInt64();
        var price = root.GetProperty("price").GetDouble();
        var qty = root.GetProperty("qty").GetDouble();
        var at = root.TryGetProperty("timestamp", out var te)
            ? DateTimeOffset.FromUnixTimeMilliseconds(te.GetInt64())
            : _clock.GetUtcNow();

        if (_books.ApplyDelta(symbol, isBid, seq, price, qty, at) == KrakenBookBuilder.DeltaResult.Gap)
        {
            _log.LogWarning("Kraken WS: seq gap on {Symbol}; book dirty, resyncing", symbol);
            _ = ResyncBookAsync(symbol);
        }
    }

    private async Task CrosscheckAsync(CancellationToken ct)
    {
        var response = await _client.GetTickersAsync(ct);
        var drifted = 0;
        foreach (var rest in response.Tickers)
        {
            if (!rest.Symbol.StartsWith(PerpPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var restMid = (rest.Bid + rest.Ask) / 2;
            // Compare REST against the WS BOOK's top-of-book, not the ticker cache: this is what
            // catches a book that has silently frozen behind a live socket, and it does not
            // false-positive on an illiquid ticker that is merely a little behind.
            if (restMid <= 0 || !_books.TryGetTopMid(rest.Symbol, out var wsMid) || wsMid <= 0)
            {
                continue;
            }

            var driftBps = Math.Abs(restMid - wsMid) / restMid * 10_000.0;
            if (driftBps > _driftBps)
            {
                drifted++;
                _books.MarkDirty(rest.Symbol);
                _ = ResyncBookAsync(rest.Symbol);
            }
        }

        if (drifted > 0)
        {
            _log.LogWarning("Kraken WS cross-check: {Count} symbols drifted past {Bps} bps; books resyncing", drifted, _driftBps);
        }
    }

    private async Task ResyncBookAsync(string symbol)
    {
        var now = _clock.GetUtcNow();
        if (_lastResync.TryGetValue(symbol, out var last) && now - last < ResyncDebounce)
        {
            return;
        }

        _lastResync[symbol] = now;
        await SendOneAsync("unsubscribe", "book", symbol, _ct);
        await SendOneAsync("subscribe", "book", symbol, _ct);
    }

    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        var instruments = await _client.GetInstrumentsAsync(ct);
        var next = instruments
            .Where(i => i.Symbol.StartsWith(PerpPrefix, StringComparison.Ordinal) && !i.IsExpired)
            .Select(i => i.Symbol)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var prev = _symbols;
        _symbols = next;

        if (!_conn.Connected)
        {
            return;   // OnOpen will subscribe the whole set on connect
        }

        var added = next.Except(prev, StringComparer.Ordinal).ToArray();
        var removed = prev.Except(next, StringComparer.Ordinal).ToArray();
        if (added.Length > 0)
        {
            await SendFeedsAsync("subscribe", added, ct);
        }

        if (removed.Length > 0)
        {
            await SendFeedsAsync("unsubscribe", removed, ct);
            foreach (var symbol in removed)
            {
                _books.Remove(symbol);
                _tickers.Remove(symbol);
            }
        }
    }

    private async Task SendFeedsAsync(string action, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        if (symbols.Count == 0)
        {
            return;
        }

        foreach (var feed in new[] { "ticker", "book" })
        {
            for (var i = 0; i < symbols.Count; i += 200)
            {
                var chunk = symbols.Skip(i).Take(200);
                var ids = string.Join(",", chunk.Select(s => $"\"{s}\""));
                await _conn.SendAsync($"{{\"event\":\"{action}\",\"feed\":\"{feed}\",\"product_ids\":[{ids}]}}", ct);
            }
        }
    }

    private Task SendOneAsync(string action, string feed, string symbol, CancellationToken ct) =>
        _conn.SendAsync($"{{\"event\":\"{action}\",\"feed\":\"{feed}\",\"product_ids\":[\"{symbol}\"]}}", ct);

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
                _log.LogWarning(ex, "Kraken WS {What} pass failed", what);
            }
        }
    }
}

internal static class KrakenJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

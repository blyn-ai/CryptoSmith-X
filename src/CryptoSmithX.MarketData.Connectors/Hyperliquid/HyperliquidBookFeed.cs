using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Streaming;
using Microsoft.Extensions.Logging;

namespace CryptoSmithX.MarketData.Connectors.Hyperliquid;

/// <summary>
/// The Phase 1 baseline: <c>l2Book</c> has no batched form on Hyperliquid (unlike WEEX, which at
/// least batches bid/ask size on its v3 clone), so bid/ask/size and depth are both per-coin calls.
/// Exactly the case WEEX's open-interest feed already solved: cycle the known coins continuously in
/// the background, pacing calls, and serve whatever was most recently learned from a
/// <see cref="MarketCache{T}"/>, rather than one blocking call per coin on every snapshot tick. One
/// fetch here answers both <see cref="TryGetTop"/> and <see cref="TryGetDepth"/> — see
/// <see cref="HyperliquidBookMath"/>. Works standalone (no WS required); <see cref="HyperliquidWsFeed"/>
/// supersedes it when a <c>ws_url</c> is configured and the socket is healthy.
/// </summary>
public sealed class HyperliquidBookFeed : IHyperliquidLiveFeed
{
    // No documented public rate limit was found during recon, but live verification hit a real 429
    // once the snapshot loop, both collectors' unpaced per-symbol bursts, and a fast book cycle all
    // overlapped at startup (see the commit verdict). This feed is the rare-case degraded fallback
    // once a WS feed is healthy, not the primary path, so it paces gently — a slow, low-priority
    // trickle rather than competing for the same budget the ticker/candle/funding calls need.
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SymbolRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly HyperliquidClient _client;
    private readonly MarketCache<(BookTop Top, Depth? Depth)> _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private volatile string[] _symbols = [];

    public HyperliquidBookFeed(HyperliquidClient client, ILoggerFactory loggers, TimeProvider clock)
    {
        _client = client;
        _clock = clock;
        _cache = new MarketCache<(BookTop, Depth?)>(clock);
        _log = loggers.CreateLogger("Hyperliquid.Book");
    }

    public void Start(CancellationToken ct) => _ = RunAsync(ct);

    public bool TryGetTop(string symbol, out BookTop top)
    {
        if (_cache.TryGet(symbol, MaxAge, out var entry))
        {
            top = entry.Top;
            return true;
        }

        top = default!;
        return false;
    }

    public bool TryGetDepth(string symbol, out Depth depth)
    {
        if (_cache.TryGet(symbol, MaxAge, out var entry) && entry.Depth is not null)
        {
            depth = entry.Depth;
            return true;
        }

        depth = default!;
        return false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastSymbolRefresh = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            if (_clock.GetUtcNow() - lastSymbolRefresh >= SymbolRefreshInterval)
            {
                await RefreshSymbolsAsync(ct);
                lastSymbolRefresh = _clock.GetUtcNow();
            }

            var symbols = _symbols;
            foreach (var symbol in symbols)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var book = await _client.GetL2BookAsync(symbol, ct);
                    var (top, depth) = HyperliquidBookMath.Compute(book, _clock.GetUtcNow());
                    if (top is not null)
                    {
                        _cache.Set(symbol, (top, depth));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One coin's failure must not stall the whole cycle; it just stays stale a little
                    // longer and the next pass retries it.
                    _log.LogDebug(ex, "Hyperliquid book fetch failed for {Symbol}", symbol);
                }

                try
                {
                    await Task.Delay(Pace, _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (symbols.Length == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RefreshSymbolsAsync(CancellationToken ct)
    {
        try
        {
            var meta = await _client.GetMetaAsync(ct);
            _symbols = meta.Universe
                .Where(u => !u.IsDelisted)
                .Select(u => u.Name)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Hyperliquid book feed: refreshing the symbol list failed; keeping the previous set");
        }
    }
}

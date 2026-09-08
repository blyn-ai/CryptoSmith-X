using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
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
    // The 800 ms that used to sit here was written when a live 429 appeared with the snapshot loop,
    // both collectors' unpaced bursts and a fast book cycle all overlapping at startup — the gate
    // did not exist yet in its current form. It does now, and it is the thing the venue reacts to,
    // so this feed no longer paces itself on top of it. Measured 2026-09-08: 768 l2Book calls at 32
    // parallel with no pause, zero refusals. Note the venue documents a WEIGHT budget (1200/min per
    // IP, l2Book costing 2) that the same measurement exceeded without complaint — see the 0036
    // migration header, which is where that disagreement is recorded and acted on.
    private static readonly TimeSpan SymbolRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>How often a pass may START. Much shorter than the open-interest feeds' minute: this
    /// one carries the live book — bid, ask and depth — for a venue with no batched form of it, so
    /// its consumer wants the newest sample, not merely one inside <see cref="MaxAge"/>. Ten seconds
    /// over 25 coins is a small share of the budget and still roughly ten times fresher than the old
    /// 800 ms trickle managed across the venue's whole universe.</summary>
    private static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly HyperliquidClient _client;
    private readonly VenueGate _gate;
    private readonly MarketCache<(BookTop Top, Depth? Depth)> _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private readonly Func<CancellationToken, Task<string[]>> _symbolsAsync;

    /// <param name="symbolsAsync">What to sample, from OUR database rather than the venue's universe:
    /// collected and trading. Hyperliquid lists 178 coins and we store 25 of them.</param>
    public HyperliquidBookFeed(
        HyperliquidClient client,
        VenueGate gate,
        Func<CancellationToken, Task<string[]>> symbolsAsync,
        ILoggerFactory loggers,
        TimeProvider clock)
    {
        _client = client;
        _gate = gate;
        _symbolsAsync = symbolsAsync;
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

    private Task RunAsync(CancellationToken ct) => SymbolCycle.RunAsync(
        "Hyperliquid.Book", _symbolsAsync, SymbolRefreshInterval, PassInterval, _gate,
        async (symbol, workCt) =>
        {
            HlL2Book book;
            using (await _gate.AcquireAsync(workCt))
            {
                book = await _client.GetL2BookAsync(symbol, workCt);
            }

            var (top, depth) = HyperliquidBookMath.Compute(book, _clock.GetUtcNow());
            if (top is not null)
            {
                _cache.Set(symbol, (top, depth));
            }
        },
        _log, _clock, ct);
}

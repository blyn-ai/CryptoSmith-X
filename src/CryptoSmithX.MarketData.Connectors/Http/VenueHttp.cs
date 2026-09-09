using System.Net;

namespace CryptoSmithX.MarketData.Connectors.Http;

/// <summary>
/// The one <see cref="HttpClient"/> every venue client's production constructor defaults to
/// (<c>WeexFuturesClient</c>, <c>BinanceUsdmClient</c>, <c>KrakenFuturesClient</c>,
/// <c>HyperliquidClient</c> — each still takes an <c>HttpClient</c> directly for tests). One instance
/// rather than four: <see cref="SocketsHttpHandler"/> already pools connections per origin
/// internally, so four venues on four different hosts do not contend for one pool by sharing this —
/// they get four pools inside one handler instead of four handlers each reinventing the same
/// settings.
///
/// TWO SETTINGS, BOTH MEASURED (plans/prompt-rest-hygiene.md, plans/collection-policy.md §5),
/// NEITHER PRESENT ANYWHERE IN THIS REPOSITORY BEFORE THIS FILE — a plain <c>new HttpClient()</c>
/// carries neither:
///
///   * AutomaticDecompression. The runtime default sends no Accept-Encoding at all, so every venue
///     that offers gzip served it uncompressed instead: Binance's full ticker batch measured 285 KB
///     against 63 compressed, Kraken's 149 against 35, Kraken's order book 46 against 13. Across the
///     full symbol lists that is 1463 GB/month against 853 — a 42% cut from one setting, on venues
///     that were already paying to compress a response we told them, by omission, we could not read.
///     WEEX does not compress at all, so this costs it nothing and helps it nothing either.
///
///   * PooledConnectionIdleTimeout. The .NET default is 1 minute, and this codebase's REST passes —
///     collector loops on their own interval, SymbolCycle-driven feeds between rounds — routinely
///     leave a connection idle that long or longer, so the pool had usually already torn the socket
///     down by the next request. Measured: a cold request costs 0.7-1.6 s against a warm one's 0.3 s
///     — the TLS handshake and TCP setup, paid again for no reason. Five minutes is comfortably above
///     every pass cadence in this codebase with room to spare, not tuned to any one of them.
///
/// One documented trade-off, not a defect: gzip has its own overhead, and on a response already
/// smaller than that overhead it can lose — Binance's <c>openInterest</c> reply measured 69 bytes
/// plain against 86 compressed. That is noise next to the batched calls above and not worth a
/// per-endpoint carve-out.
/// </summary>
public static class VenueHttp
{
    public static readonly HttpClient Shared = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    });
}

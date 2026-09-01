using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

/// <summary>
/// What the adapter needs from the live WS feed: a fresh ticker slice and per-symbol depth, each
/// returning false when the feed cannot honestly serve it so the adapter falls back to REST. An
/// interface so the adapter's WS-first / REST-fallback branch is testable without a socket.
/// </summary>
public interface IKrakenLiveFeed
{
    bool TryGetFreshTickers(out IReadOnlyList<Ticker> tickers);

    bool TryGetDepth(string symbol, out Depth depth);
}

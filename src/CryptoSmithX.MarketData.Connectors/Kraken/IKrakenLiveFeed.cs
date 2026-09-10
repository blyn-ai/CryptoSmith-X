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

    /// <summary>Trades buffered since the last call — see <see cref="IExchangeMarketData.DrainTrades"/>.
    /// Defaulted so a test double that only cares about tickers stays a two-line class.</summary>
    IReadOnlyList<TradeEvent> DrainTrades() => [];

    /// <summary>A lossy second reader of the same tape, for the live path — see
    /// <see cref="IExchangeMarketData.ObserveTrades"/>. Null on a feed with no socket behind it.</summary>
    Streaming.EventTap<TradeEvent>? ObserveTrades(int capacity) => null;

    bool TryGetBookFrame(string symbol, int levels, out BookFrame frame)
    {
        frame = null!;
        return false;
    }
}

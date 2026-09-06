namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// What the adapter needs from the background open-interest cycle: the most recent sample for a
/// symbol and when it was taken, or false when none exists yet — so the ticker for that symbol waits
/// rather than being written with a fabricated value. An interface, not the concrete feed, so the
/// adapter's merge-and-skip logic is testable without driving the real cycle; the same seam
/// <see cref="Weex.IWeexOpenInterestFeed"/> already provides on the other venue with no batched OI.
/// </summary>
public interface IBinanceOpenInterestFeed
{
    bool TryGet(string symbol, out double openInterest, out DateTimeOffset at);
}

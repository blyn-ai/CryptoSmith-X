namespace CryptoSmithX.MarketData.Connectors.Weex;

/// <summary>
/// What the adapter needs from the background open-interest cycle: the most recent sample for a
/// symbol and when it was taken, or false when none exists yet — so the ticker for that symbol waits
/// rather than being written with a fabricated value. An interface so the adapter's merge-and-skip
/// logic is testable without driving the real background cycle.
/// </summary>
public interface IWeexOpenInterestFeed
{
    bool TryGet(string symbol, out double openInterest, out DateTimeOffset at);
}

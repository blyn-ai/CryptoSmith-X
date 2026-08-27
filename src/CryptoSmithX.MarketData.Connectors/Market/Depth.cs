namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// Cumulative notional in the quote asset within a band of the mid price, one sum per side.
/// Null means "not measured": either the venue returned no book, or the deepest level received
/// was still inside the band, which would make the sum an undercount.
/// The book is a per-symbol call, so it carries its own timestamp.
/// </summary>
public sealed record Depth(
    double? Bid10Bps,
    double? Ask10Bps,
    double? Bid25Bps,
    double? Ask25Bps,
    double? Bid50Bps,
    double? Ask50Bps,
    DateTimeOffset At);

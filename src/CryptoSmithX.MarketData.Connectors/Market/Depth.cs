namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// Cumulative notional in the quote asset within a band of the mid price, one sum per side.
/// Null means "not measured": either the venue returned no book, or the deepest level received
/// was still inside the band, which would make the sum an undercount.
/// The book is a per-symbol call, so it carries its own timestamp.
/// </summary>
/// <param name="Mid">The mid price the bps bands (and <see cref="ReachBidBps"/>/<see cref="ReachAskBps"/>)
/// were computed from — without it the bands are not reproducible from the row alone
/// (market_snapshot.depth_ref, 0030).</param>
/// <param name="ReachBidBps">How far the bid side of the collected book actually extends, in bps
/// from <see cref="Mid"/>, to the worst (lowest-price) level present. 0 when the side has no
/// levels — the measurement happened and found nothing, which 0030's column comment says must
/// stay distinguishable from "depth was not collected this frame" (a null <see cref="Depth"/>
/// entirely, at the <see cref="Ticker"/> level).</param>
/// <param name="ReachAskBps">Same as <see cref="ReachBidBps"/>, ask side: bps from <see cref="Mid"/>
/// to the worst (highest-price) level present, 0 when empty.</param>
public sealed record Depth(
    double? Bid10Bps,
    double? Ask10Bps,
    double? Bid25Bps,
    double? Ask25Bps,
    double? Bid50Bps,
    double? Ask50Bps,
    DateTimeOffset At,
    double? Mid = null,
    double ReachBidBps = 0,
    double ReachAskBps = 0);

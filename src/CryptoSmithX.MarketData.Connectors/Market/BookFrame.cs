namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// The top N levels of one instrument's book at one instant. Mirrors <c>book_topn</c> (0032).
///
/// A FRAME, not a diff: this is always the maintained book's current top, so
/// <see cref="IsSnapshot"/> is true for everything written from a polled read. The column exists
/// because the schema also allows a delta writer; there is not one, and pretending a poll is a
/// delta would misstate what the row is.
/// </summary>
/// <param name="ObservedAt">The venue's own clock for the frame the levels came from — the last
/// book message applied, not our read time.</param>
/// <param name="Seq">The venue's sequence number for that message. Part of the primary key with
/// observed_at, so two frames sharing an instant stay distinct.</param>
/// <param name="Levels">How deep we asked, not how deep the book answered — the arrays may be
/// shorter on a thin side, which is exactly what the schema's cardinality CHECK allows.</param>
/// <param name="BidCounts">Orders per level where the venue publishes it. None of the four do at
/// this scale, so this is null everywhere today rather than a fabricated 1-per-level.</param>
public sealed record BookFrame(
    string ExchangeSymbol,
    DateTimeOffset ObservedAt,
    long Seq,
    bool IsSnapshot,
    int Levels,
    IReadOnlyList<double> BidPrices,
    IReadOnlyList<double> BidQuantities,
    IReadOnlyList<double> AskPrices,
    IReadOnlyList<double> AskQuantities,
    IReadOnlyList<int>? BidCounts = null,
    IReadOnlyList<int>? AskCounts = null);

namespace CryptoSmithX.Arena.Models;

/// <summary>
/// The statement line: how many books quote this pair, and the worst thing that is true about the
/// page's freshness.
///
/// <b>Why it is a class and not five lines in the view.</b> Two renderers say this sentence — this
/// one, on the server, for the first paint and for every live push, and <c>arena-ages.js</c>, which
/// re-derives it every second from the same instants it advances the ages from. A sentence that
/// lives only inside a Razor <c>@functions</c> block is a sentence nothing in CI can read, and the
/// two copies drifting apart is precisely the failure this file was written to close: the server's
/// version said "every call is inside its window" for three minutes while every age under it had
/// already flipped to <c>degraded</c>. The words are constants here so a test can assert the client
/// still says the same ones.
///
/// The counts are here for the second half of the same reason. The page used to count ROWS and call
/// them venues and platforms; the list page argues at length that those are three different numbers
/// and prints two of them on the card the reader just clicked. Counting them in one place, with the
/// list page's own definitions, is the only way the two pages can agree.
/// </summary>
public static class Statement
{
    /// <summary>Nothing is wrong, said plainly. A statement line that is decorative on a quiet page
    /// is a statement line nobody believes on a loud one.</summary>
    public const string InsideTheWindow = "Every call is inside its window.";

    /// <summary>Discovery has listed the instruments and no collector has ever written a row.
    /// Nothing is late, because nothing has happened.</summary>
    public const string NeverObserved = "Nothing here has been observed yet.";

    /// <summary>Observations with no stated cadence to judge them against, so they are not
    /// judged.</summary>
    public const string NoCadence = "None of them states how often it looks.";

    public const string OneFeedDegraded = "One feed has stopped meaning anything.";

    public static string FeedsDegraded(int n) => $"{n} feeds have stopped meaning anything.";

    public static string OldestBehind(string label, int seconds) =>
        $"The oldest is {label.ToLowerInvariant()}, {seconds} seconds behind.";

    /// <summary>
    /// The accent half of the statement line.
    ///
    /// It is generated from the data and it is never a slogan. The design system's own example —
    /// "SEVEN VENUES QUOTE AR/USD. ONE BOOK IS NINETY SECONDS OLD." — reads as copywriting and is
    /// not: it is the oldest call on the page, said out loud. So this returns the worst thing true
    /// about the page's freshness at the instant the ages beside it were computed, and when nothing
    /// is wrong it says that instead of reaching for something dramatic.
    /// </summary>
    public static string Verdict(IReadOnlyList<VenueRowModel> rows)
    {
        var strips = rows.Select(StripModel.Build).ToList();

        if (strips.Any(s => s.Degraded))
        {
            var n = strips.Count(s => s.Degraded);
            return n == 1 ? OneFeedDegraded : FeedsDegraded(n);
        }

        var late = strips
            .SelectMany(s => s.Calls)
            .Where(c => c.PastWindow && c.AgeSeconds is not null)
            .ToList();

        if (late.Count > 0)
        {
            var worst = late.MaxBy(c => c.AgeSeconds!.Value)!;
            return OldestBehind(worst.Label, (int)Math.Round(worst.AgeSeconds!.Value));
        }

        // Two different silences, and they are not the same sentence. No calls at all means discovery
        // has listed the instrument and no collector has ever written a row for it — nothing is late,
        // because nothing has happened. Calls with no window means we have observations and no stated
        // cadence to judge them against, so we decline to judge them.
        var landed = strips.SelectMany(s => s.Calls).ToList();
        if (landed.Count == 0)
        {
            return NeverObserved;
        }

        return landed.All(c => c.WindowSeconds is null) ? NoCadence : InsideTheWindow;
    }

    /// <summary>
    /// Order books quoting this pair — distinct SEGMENTS, which is what the pair list means by
    /// "venue" and what the card the reader clicked to get here printed.
    ///
    /// Not the row count. A row is a listing, and one segment can list BTC/USDT and BTC/USDC at
    /// once; both fold onto this page and both keep their own book. Counting rows and calling them
    /// venues told a reader that four exchanges quote the pair when Binance was in the table twice
    /// — an overstatement of the breadth of the comparison, which is the one thing this page sells.
    /// </summary>
    public static int Venues(IReadOnlyList<VenueRowModel> rows) =>
        rows.Select(r => r.Row.SegmentCode).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Exchanges. Fewer than <see cref="Venues"/> wherever one exchange has more than one
    /// enabled segment — a spot book and a perp book are two venues on one platform.</summary>
    public static int Platforms(IReadOnlyList<VenueRowModel> rows) =>
        rows.Select(r => r.Row.ExchangeCode).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Instruments. One row of the table each, and the number the perp / spot split
    /// below it is a split of.</summary>
    public static int Listings(IReadOnlyList<VenueRowModel> rows) => rows.Count;

    public static int Perps(IReadOnlyList<VenueRowModel> rows) =>
        rows.Count(r => !string.Equals(r.Row.SegmentKind, "spot", StringComparison.Ordinal));
}

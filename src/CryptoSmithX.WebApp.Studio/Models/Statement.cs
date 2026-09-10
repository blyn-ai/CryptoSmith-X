namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// The statement line: how many books quote this pair, and the worst thing that is true about the
/// page's freshness.
///
/// <b>Why it is a class and not five lines in the view.</b> Two renderers say this sentence — this
/// one, on the server, for the first paint and for every live push, and <c>studio-ages.js</c>, which
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

    /// <summary>
    /// A call has run past its own window and none has gone degraded — the middle rung of the
    /// ladder, and it NAMES A STATE rather than counting a clock.
    ///
    /// <b>What it replaced, and why that had to go.</b> It read "The oldest is price, 77 seconds
    /// behind.", and the count in it was live: <c>studio-ages.js</c> re-derives this sentence every
    /// second, so the largest type on the page rewrote itself once a second for the life of the tab.
    /// In Anton — the display face this line is set in, which ships no <c>tnum</c> and no
    /// <c>pnum</c>, so <c>font-variant-numeric</c> is inert on it — "1" advances 677/2048 and every
    /// other digit 1012/2048. At 26px that is 4.25px of jitter per digit per second, and ~13px when
    /// the count crosses nine. The headline flickered and changed width, which is precisely what
    /// rule 10 forbids: the only thing on this surface that moves on its own is an age, because an
    /// age is a clock, and a display headline is not an age line. The seconds are already stated,
    /// to the second, in the age line under every figure the sentence is about.
    ///
    /// So the sentence says the STATE — how many calls are past their windows — and changes when a
    /// call crosses that boundary, which is an event and not a tick. Two further defects went with
    /// the old wording: it had no singular form, so "1 seconds behind" was reachable, and
    /// <c>label.ToLowerInvariant()</c> put "oi" in the middle of an English sentence.
    ///
    /// <b>Rejected:</b> keeping the count and putting it in a fixed-width span with tabular figures.
    /// The owner accepts visible slack over motion and it would have held the width — but a slot
    /// only stops the LINE from moving; the digits inside it still change once a second in 26px
    /// type, which is the flicker that was reported. Rule 10 is not about layout stability, it is
    /// about what is allowed to move at all.
    /// </summary>
    public const string OneCallLate = "One call is past its window.";

    public static string CallsLate(int n) => $"{n} calls are past their windows.";

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

        // P0-4 (UX audit): the count IS the array — LateCalls, below — rather than a second
        // Count(...) that could drift from what band 5's gap list and band 1's per-cell rule are
        // showing for the exact same instant. Counted, not ranked: the sentence used to pick the
        // oldest call and print its age, which made the headline a clock; what the reader needs
        // from the largest type on the page is which of the four states the page is in, and the
        // age of every one of these calls is already printed under the figure it dates.
        var late = LateCalls(rows).Count;

        if (late > 0)
        {
            return late == 1 ? OneCallLate : CallsLate(late);
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
    /// The same four facts as <see cref="Verdict"/>, handed back as NUMBERS instead of a sentence.
    ///
    /// <b>Why both exist.</b> Verdict picks one of five sentences, so the headline changes length
    /// whenever the page changes state — and on the second asset page that line is the largest type
    /// on the screen, so it visibly jumped. The counts let the words stay put and only the figures
    /// move, each in a slot of its own.
    ///
    /// <b>What that costs, and how it is paid.</b> The comment on <see cref="OneCallLate"/> rejected
    /// exactly this once, and its reason was real: the display face ships no <c>tnum</c>, so digits
    /// in it have different advances and a fixed slot holds the LINE still while the digits inside
    /// keep shifting. The answer is not to argue with that — it is to set the figures in the mono
    /// face, which does have tabular figures. The words stay in the display face; only the numbers
    /// leave it, and they are the only part that changes.
    ///
    /// Null is not zero here either: a page where nothing has ever been observed, or where nothing
    /// states a cadence, has NO count — zero would claim everything is inside its window, which is
    /// the one thing those two states do not say.
    /// </summary>
    public static StatementCounts Counts(IReadOnlyList<VenueRowModel> rows)
    {
        var strips = rows.Select(StripModel.Build).ToList();
        var landed = strips.SelectMany(s => s.Calls).ToList();

        if (landed.Count == 0 || landed.All(c => c.WindowSeconds is null))
        {
            return new StatementCounts(null, null);
        }

        return new StatementCounts(LateCalls(rows).Count, strips.Count(s => s.Degraded));
    }

    /// <summary>
    /// P0-4 (UX audit): every call on the page that is past its own window, right now — the ONE
    /// array <see cref="Verdict"/>'s count, band 1's per-cell rule (V2Cells.cs), and band 5's
    /// synthetic OPEN gap rows (V2Coverage / Asset.cshtml) all read, so the three can never
    /// disagree about which calls are late or how many there are. <see cref="Freshness.PastWindow"/>
    /// is the one predicate underneath all three; this is that predicate walked once over the
    /// whole page and handed back as data instead of a count.
    /// </summary>
    public static IReadOnlyList<(VenueRowModel Row, StripCall Call)> LateCalls(IReadOnlyList<VenueRowModel> rows) =>
        rows.SelectMany(r => StripModel.Build(r).Calls
                .Where(c => c.PastWindow && c.AgeSeconds is not null)
                .Select(c => (Row: r, Call: c)))
            .ToList();

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

/// <summary>The statement line as figures: how many calls are past their windows and how many feeds
/// have stopped meaning anything. Null in both when the page has nothing to judge — see
/// <see cref="Statement.Counts"/>.</summary>
public sealed record StatementCounts(int? Late, int? Degraded)
{
    /// <summary>What a slot prints. A dash, never a zero, when there is no count to give.</summary>
    public static string Slot(int? n) => n?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}

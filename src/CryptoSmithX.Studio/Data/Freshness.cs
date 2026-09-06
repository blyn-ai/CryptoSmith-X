namespace CryptoSmithX.Studio.Data;

/// <summary>
/// The fade, the △ and the word <c>degraded</c> — rule 2 of the design system, as arithmetic.
///
/// The window is never a constant here. It arrives per figure from
/// <see cref="Models.SegmentFreshness"/>, because price, open interest and the depth sweep are three
/// calls with three clocks and a flat number for all three is the bug this file is written around.
/// The design system's own text names 30 s, and 30 s is right for a price on a ten-second ticker and
/// wrong by an order of magnitude for a depth band on a venue whose pass takes 361 s.
///
/// The server computes these for the first paint. The moving age belongs to the client, which
/// anchors on performance.now() against the server instant (blueprint §5) — otherwise a visitor
/// whose clock runs forty seconds fast reads the whole page as degraded.
/// </summary>
public static class Freshness
{
    /// <summary>How faint a spent figure gets. Never zero: a figure that has faded to nothing is
    /// indistinguishable from a figure that was never there, and those are different sentences.</summary>
    public const double Floor = 0.15;

    /// <summary>Front-loads the fade, so the first seconds cost more than the last.</summary>
    public const double Exponent = 0.4;

    /// <summary>Past this many windows the count has stopped meaning anything and the word replaces
    /// the number: thirty-one seconds and thirty days are the same verdict.</summary>
    public const int DegradedWindows = 12;

    /// <summary>
    /// Opacity for a figure of this age, judged against the window of the call that wrote it.
    ///
    /// The amplitude is <c>1 − Floor</c>, derived rather than written down. The design system's
    /// prose gives it as 0.85, which is the same number today and would silently stop being the same
    /// number the first time anyone moved the floor — a fourth constant that has to be kept in
    /// agreement with a third by hand.
    ///
    /// A figure with no known window does not fade. We cannot say how old is old for a call whose
    /// cadence the database does not state, and a guess would be this page inventing the one thing
    /// it is here to report.
    /// </summary>
    public static double Weight(double? ageSeconds, double? windowSeconds)
    {
        if (ageSeconds is not { } age || windowSeconds is not { } window || window <= 0)
        {
            return 1.0;
        }

        // Clamped at both ends. Below zero because a venue clock can legitimately run ahead of ours
        // — received_at is Kraken's own clock, not ours (SnapshotCollector) — and a negative age is
        // not evidence of anything. Above one because past the window nothing is graded further.
        var spent = Math.Clamp(age / window, 0.0, 1.0);
        return Math.Max(Floor, 1.0 - (1.0 - Floor) * Math.Pow(spent, Exponent));
    }

    /// <summary>Whether the △ belongs beside the age: the call is past the window it is judged
    /// against, so the number is no longer being graded.</summary>
    public static bool PastWindow(double? ageSeconds, double? windowSeconds) =>
        ageSeconds is { } age && windowSeconds is { } window && window > 0 && age >= window;

    /// <summary>Whether the count is dropped for the word.</summary>
    public static bool Degraded(double? ageSeconds, double? windowSeconds) =>
        ageSeconds is { } age && windowSeconds is { } window && window > 0
        && age >= window * DegradedWindows;

    /// <summary>
    /// Age in seconds of an instant against the time of the request.
    ///
    /// Against the REQUEST, never against the moment the cache filled. The instants in a cached
    /// payload are absolute, which is the entire reason a payload may be reused at all: a row
    /// served from a 900 ms-old cache still reports a truthful age, but only if the subtraction is
    /// done here, per request. Null in, null out — an instant we do not have has no age, and the
    /// figure beside it is a dash rather than a zero.
    /// </summary>
    public static double? AgeSeconds(DateTime? instant, DateTimeOffset now)
    {
        if (instant is not { } t)
        {
            return null;
        }

        // Npgsql hands back timestamptz as Kind=Utc, so the first branch is the one that runs. The
        // second exists because a value that reached us some other way and carries no zone must be
        // read as UTC rather than as the server's local time — every instant in this schema is
        // timestamptz, and quietly shifting one by the host's offset would be an age off by hours
        // on a page whose subject is the age.
        var utc = t.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : t.ToUniversalTime();

        return (now - new DateTimeOffset(utc)).TotalSeconds;
    }
}

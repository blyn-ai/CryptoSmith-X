namespace CryptoSmithX.WebApp.Studio.Data;

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

    /// <summary>The lowest a late threshold is ever allowed to sit, regardless of window or
    /// cadence.
    ///
    /// A window can compute tighter than a reader's own request-to-render gap: a live push, a slow
    /// network hop, or a segment whose measured pass is briefly near zero can all put a call inside
    /// a window shorter than twenty seconds even though it is landing exactly on schedule. Twenty
    /// seconds is not a guess — it is comfortably inside "just looked" for every call this page
    /// judges and comfortably outside a render's own jitter.</summary>
    public const double LateFloorSeconds = 20.0;

    /// <summary>How many of a call's OWN cadences must pass before that call counts as late,
    /// regardless of what its computed window says.
    ///
    /// <see cref="Models.SegmentFreshness.Window"/> already folds a measured pass into the window,
    /// but a segment can report a pass at or near zero — early in collection, or between two
    /// perfectly synchronised polls — and a window built from cadence alone is the bare interval,
    /// with none of the headroom a real pass earns it. Three cadences is the floor under that
    /// window: a call that has run three times since it last wrote has not merely been polled once
    /// more than expected, it has been missed.</summary>
    public const double LateCadenceMultiplier = 3.0;

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
    /// against, so the number is no longer being graded.
    ///
    /// <paramref name="cadenceSeconds"/> is the call's own configured interval — never the
    /// computed window, which already has cadence baked into it (see
    /// <see cref="Models.SegmentFreshness.Window"/>). It raises the threshold rather than lowers
    /// it: a window computed tighter than <see cref="LateCadenceMultiplier"/> cadences, or tighter
    /// than <see cref="LateFloorSeconds"/>, is not evidence a call is late, only evidence the window
    /// was computed from a thin sample. Optional and defaulting to null — a caller with no cadence
    /// to offer still gets the twenty-second floor and nothing else, which is exactly today's
    /// behaviour with one extra guard against a too-tight window.</summary>
    public static bool PastWindow(
        double? ageSeconds, double? windowSeconds, double? cadenceSeconds = null, bool? marketOpen = null)
    {
        // A market the venue says is shut cannot be late: nothing is due from it. Checked before
        // the window rather than after, because every threshold below is an argument about a feed
        // that is supposed to be answering.
        if (Closed(marketOpen))
        {
            return false;
        }

        if (ageSeconds is not { } age || windowSeconds is not { } window || window <= 0)
        {
            return false;
        }

        var threshold = Math.Max(Math.Max(window, (cadenceSeconds ?? 0) * LateCadenceMultiplier), LateFloorSeconds);
        return age >= threshold;
    }

    /// <summary>
    /// A CLOSED market is not a late one, and this is the whole of the difference.
    ///
    /// Freshness measures how long ago a figure was observed, and every judgement built on it — the
    /// magenta cell, the header's "past window" count, the degraded word — assumes that a figure
    /// which stopped moving means a feed that stopped answering. On a market with trading hours
    /// that assumption is simply false: the oracle keeps publishing a price no one can trade at,
    /// and by Monday a perfectly healthy venue would be the reddest thing on the page.
    ///
    /// So the age is still SHOWN — the reader is owed the number — and it is not JUDGED. Null means
    /// the venue publishes no such flag, which is every venue but Avantis, and those are judged
    /// exactly as they were: an unknown session is not an excuse.
    /// </summary>
    public static bool Closed(bool? marketOpen) => marketOpen is false;

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

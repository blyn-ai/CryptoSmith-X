using System.Globalization;
using CryptoSmithX.Arena.Data;

namespace CryptoSmithX.Arena.Models;

/// <summary>One call's place on the row's freshness scale.</summary>
/// <param name="Position">
/// Where its tick sits, 0 at the green end and 1 at the magenta one — the age as a fraction of its
/// OWN window, which is why three calls on one row can sit in three different places while showing
/// three ages that look similar, or sit together while showing ages minutes apart. That is the
/// point of the strip.
/// </param>
/// <param name="Placed">
/// False when the call has an age but no known window. It still appears in the named list below the
/// scale, with its age; it gets no tick, because there is nothing to measure it against and putting
/// it anywhere on the scale would be picking a window out of the air.
/// </param>
/// <param name="Spent">
/// The same fraction UNCLAMPED, and null where the call is not placed. <see cref="Position"/> is
/// clamped to the scale because a tick cannot be drawn off the end of it; this is what the two ends
/// are chosen by, so that two calls both past their windows are ordered by which is further past
/// rather than by which the enumeration reached first.
/// </param>
public sealed record StripCall(
    string Label,
    double? AgeSeconds,
    double? WindowSeconds,
    long? InstantMs,
    double Position,
    bool Placed,
    bool PastWindow,
    double? Spent);

/// <summary>
/// The row's own freshness, as it appears in the venue cell: a scale from the call that has just
/// landed to the call that is spent, a tick per call, and each call named with its age.
///
/// It exists because inside one platform the three calls answer at different rates, and a single
/// "row age" would be an average of three clocks — the one number this whole surface is built to
/// refuse. Rule 1: a figure with no age is a claim with no date, and the row's summary has to be
/// three dates or none.
///
/// <b>THE TWO END LABELS ARE ON THE SCALE THEY SIT UNDER.</b> They were not. They were
/// <c>ages.Min()</c> and <c>ages.Max()</c> — RAW SECONDS — printed under the two ends of a gradient
/// whose ticks, whose span bar and whose △ are all fractions of each call's OWN window, and the △
/// itself was attached to whichever call had the largest raw age rather than to a call that was
/// actually past its window. Under the windows this deployment really has that inverts on an
/// ordinary row: a price call 23 s old against a 10 s cadence sits at the spent end of the scale
/// while the depth sweep 38 s old against a 300 s pass sits at an eighth of its own, and the strip
/// printed "fresh 23 s" on the green end and "△ old 38 s" in the hold ink on the magenta one. Both
/// labels named the call at the opposite end of the gradient, and the mark accused the healthy call.
///
/// This is the one place the port DIVERGES from <c>FreshnessStrip.jsx</c> and it has to. That
/// component takes ONE <c>windowSeconds</c> for all three calls, and under one window the smallest
/// age and the smallest fraction are the same call, so min/max over the raw ages is correct there
/// and is the same selection as this. Rule 1 gives this model three windows, and under three the two
/// selections are different questions.
///
/// So the ends name the CALL at each end, with its age. Rejected: keeping "fresh N s" / "old N s"
/// and only changing which call the number comes from — the numbers then do not sort, and "fresh
/// 38 s" beside "old 23 s" is a puzzle rather than a reading, because the words claim an ordering
/// of ages that the ends are not an ordering of. Rejected: printing the fraction instead of the age
/// ("Price 230%"), which is exactly the axis but introduces a unit that appears nowhere else on the
/// surface. The call names are already the vocabulary of the row — the named list below the scale
/// uses them, the header bands use them, and the cell titles use them.
/// </summary>
/// <param name="LeastSpent">
/// The call at the green end: the smallest share of its own window. Null when no call on this row
/// states a cadence, which is a row with no scale rather than a row whose scale has ends — the
/// gradient under it is empty for the same reason, and the ages are all in the named list below.
/// </param>
/// <param name="MostSpent">The call at the magenta end: the largest share of its own window.</param>
public sealed record StripModel(
    IReadOnlyList<StripCall> Calls,
    StripCall? LeastSpent,
    StripCall? MostSpent,
    bool Degraded)
{
    public IReadOnlyList<StripCall> Placed => Calls.Where(c => c.Placed).ToList();

    /// <summary>Whether the magenta end's own call is past its own window — which is what the △ and
    /// the hold ink there mean. It used to be <c>calls.Any(c =&gt; c.PastWindow)</c>, so the mark
    /// landed on the label of whichever call had the largest raw age whether or not that call was
    /// the late one.</summary>
    public bool MostSpentPastWindow => MostSpent?.PastWindow ?? false;

    /// <summary>An end label: the call's name and its age. Empty where there is no scale.</summary>
    public static string EndText(StripCall? call) =>
        call is null ? "" : call.Label + " " + Format.ShortAge(call.AgeSeconds);

    /// <summary>
    /// An end label's hover, which is where the fraction the end was CHOSEN by is written out. The
    /// label prints an age because that is the row's vocabulary; the title says what the age is a
    /// share of, so the reader can see why 38 s is at the green end and 23 s at the magenta one.
    /// </summary>
    public static string EndTitle(StripCall? call, string which) =>
        call is null
            ? "No call on this row states how often it looks, so the scale has no ends"
            : which + ": " + call.Label.ToLowerInvariant() + ", " + Format.ShortAge(call.AgeSeconds)
                + " into its " + Window(call.WindowSeconds) + " s window";

    /// <summary>The green end's word, and the magenta end's, as constants — <c>arena-ages.js</c>
    /// rewrites both every second and a test reads this file and that one for the same strings.</summary>
    public const string LeastSpentLabel = "Least spent";

    public const string MostSpentLabel = "Most spent";

    private static string Window(double? seconds) =>
        seconds is { } s ? s.ToString("0", CultureInfo.InvariantCulture) : Format.Dash;

    public double SpanFrom => Placed.Count == 0 ? 0 : Placed.Min(c => c.Position);

    public double SpanTo => Placed.Count == 0 ? 0 : Placed.Max(c => c.Position);

    public static string Pct(double fraction) =>
        (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>The span bar's width. Floored at a hair so a row whose three calls landed together
    /// still draws a mark rather than nothing — they are at one point, and one point is a fact.</summary>
    public string SpanWidth => Pct(Math.Max(0.009, SpanTo - SpanFrom));

    public static StripModel Build(VenueRowModel v)
    {
        var w = v.Windows;
        var a = v.Ages;
        var r = v.Row;

        var calls = new List<StripCall>();
        Add("Price", a.PriceSeconds, w.PriceSeconds, r.ReceivedAt);
        Add("Depth", a.DepthSeconds, w.DepthSeconds, r.DepthAt);
        Add("OI", a.OpenInterestSeconds, w.OpenInterestSeconds, r.OpenInterestAt);

        void Add(string label, double? age, double? window, DateTime? instant)
        {
            if (age is null)
            {
                // A call that has never landed for this instrument is not a stale call and it is not
                // a fresh one. It is absent, and the strip says nothing about it rather than putting
                // it at either end — a tick at the magenta end would read as "very old", which is a
                // measurement we do not have.
                return;
            }

            var placed = window is { } win && win > 0;
            var spent = placed ? age.Value / window!.Value : (double?)null;
            var position = placed ? Math.Clamp(spent!.Value, 0, 1) : 0;

            calls.Add(new StripCall(
                label, age, window, Ms(instant), position, placed,
                Freshness.PastWindow(age, window), spent));
        }

        // "Degraded" is a per-call verdict, so the row is degraded when its OLDEST call is past
        // twelve of ITS OWN windows — not when some flat multiple of some flat window has passed.
        // A row whose depth sweep is fifty minutes behind on a venue whose pass takes six is
        // degraded; the same fifty minutes on a venue that sweeps daily is not.
        var degraded = calls.Any(c => Freshness.Degraded(c.AgeSeconds, c.WindowSeconds));

        // Chosen by share of the call's OWN window, and only from the calls that are on the scale at
        // all. A call with no stated cadence has no place on the gradient — it gets no tick for that
        // reason — so it cannot be at either end of one either; its age is in the named list below,
        // where an ungraded observation belongs.
        var placedCalls = calls.Where(c => c.Spent is not null).ToList();

        return new StripModel(
            calls,
            placedCalls.Count == 0 ? null : placedCalls.MinBy(c => c.Spent!.Value),
            placedCalls.Count == 0 ? null : placedCalls.MaxBy(c => c.Spent!.Value),
            degraded);
    }

    private static long? Ms(DateTime? instant) =>
        instant is { } t
            ? new DateTimeOffset(t.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
                : t.ToUniversalTime()).ToUnixTimeMilliseconds()
            : null;
}

namespace CryptoSmithX.Arena.Models;

/// <summary>
/// The header's COLLECTED mark: the two ends of the span of instants the page was built from, as the
/// two strings that go on the screen.
///
/// <b>Why it is a class and not four lines in the partial.</b> It was four lines in the partial, and
/// they were wrong in a way nothing could fail on. The decision "are these one instant or two" was
/// taken by comparing the two FORMATTED clocks — <c>HH:mm:ssZ</c>, which carries no date — so a page
/// whose oldest observation was three days behind its freshest, at the same second of the day,
/// compared equal, collapsed the span, and printed the freshest end alone with the oldest gone from
/// the DOM entirely. That is verbatim the failure the span was introduced to remove: the healthiest
/// row standing in for the whole table. A rule that can fail that way has to live where xunit can
/// reach it (blueprint §1, and the same argument <c>RowCells</c> makes for the cells).
/// </summary>
/// <param name="Text">What the stamp reads.</param>
/// <param name="Title">Its hover, which is always the fuller statement — both ends, dated.</param>
public readonly record struct CollectedStamp(string Text, string Title)
{
    /// <summary>
    /// The two ends as printed.
    ///
    /// Three shapes, and each is a different fact:
    ///
    /// <b>Nothing observed.</b> A dash and nothing else. Never the render instant standing in for a
    /// collection instant — those are different facts and one of them is not an observation.
    ///
    /// <b>One instant.</b> The two ends are the same second, so the span is the point it is. The
    /// comparison is on the INSTANTS truncated to the second the clock prints, not on the strings:
    /// truncating is what makes "narrower than the clock's own precision" collapse, and comparing
    /// instants is what stops three days collapsing with it.
    ///
    /// <b>Two instants.</b> Both ends, and the DATE comes with them the moment they fall on
    /// different UTC days. A bare wall clock is honest only inside one day; across midnight
    /// "15:30:11Z – 15:33:18Z" is a three-day span that every reader parses as three minutes, which
    /// is the same act as printing a zero where nothing was measured — a true-looking number
    /// standing where the truth is bigger. Inside one day the date is left off, because repeating
    /// today's date twice in a header is noise and the RENDERED stamp beside it already dates the
    /// document.
    /// </summary>
    public static CollectedStamp Of(DateTime? from, DateTime? to)
    {
        if (from is null && to is null)
        {
            return new CollectedStamp(Format.Dash, "Nothing on this page has ever been observed");
        }

        // Both ends or neither: a span with one end known is not a span, and the known end alone
        // would be the healthiest-row-for-the-whole-table claim again, made by an absence.
        if (from is null || to is null)
        {
            var known = from ?? to;
            return new CollectedStamp(
                Format.UtcClock(known),
                "One end of this page's collection span is not known · " + Format.Utc(known));
        }

        var lo = Second(from.Value);
        var hi = Second(to.Value);

        if (lo == hi)
        {
            return new CollectedStamp(
                Format.UtcClock(to),
                "Every observation on this page landed at " + Format.Utc(to)
                    + " — across all three calls on every row");
        }

        var dated = lo.Date != hi.Date;
        var text = dated
            ? Format.Utc(from) + " – " + Format.Utc(to)
            : Format.UtcClock(from) + " – " + Format.UtcClock(to);

        return new CollectedStamp(
            text,
            "Oldest observation on this page " + Format.Utc(from)
                + " · freshest " + Format.Utc(to) + " — across all three calls on every row");
    }

    /// <summary>
    /// The instant at the precision the clock prints it to.
    ///
    /// Read as UTC by the same convention <see cref="Format.Utc"/> uses, so the value compared here
    /// is the value rendered. A comparison done on one reading of the kind and a render done on
    /// another is how two identical strings come to disagree.
    /// </summary>
    private static DateTime Second(DateTime t)
    {
        var utc = t.ToUniversalTime();
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}

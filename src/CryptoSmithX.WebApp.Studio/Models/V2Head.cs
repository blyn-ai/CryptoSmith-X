using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// A rank, as the collapsed row states it: a WORD with a fill behind it, never a fill alone.
///
/// <b>Why the word is the thing.</b> Colour on this page means the CALL that wrote a figure — acid
/// green is the open-interest call (rule 5) — so a green wash with no word reads as "this came from
/// the open-interest call", which is a different sentence and sometimes a false one. The fill is the
/// word's ground, not the statement.
/// </summary>
public sealed record V2Mark(Verdict Rank, string Word)
{
    public string Class => Word;

    public static V2Mark? Of(VerdictTable verdicts, int instrumentId, PairColumn column, bool spread = false)
    {
        var rank = verdicts.Of(instrumentId, column);

        return rank switch
        {
            // A spread's "best" is a width, and a reader reads TIGHT faster than they translate
            // BEST into one.
            Verdict.Best => new V2Mark(rank, spread ? "tight" : "best"),
            Verdict.Worst => new V2Mark(rank, spread ? "wide" : "worst"),
            _ => null,
        };
    }
}

/// <summary>
/// One printed figure inside a cell, with the rank it holds if it holds one.
///
/// <paramref name="Column"/> is the ranked column this figure IS, where it is one. The view has no
/// use for it — the mark is already resolved — but it is what lets a test ask "is every column
/// Verdicts ranks actually visible somewhere", which is the assertion that stops a rank being
/// computed for a figure nobody prints.
/// </summary>
public sealed record V2Part(
    string Text, V2Mark? Mark = null, bool Measured = true, PairColumn? Column = null);

/// <summary>
/// The collapsed cell: what stands large, what stands under it, and what the bar measures.
///
/// <b>Large is the figure the column is named after.</b> The price column printed the SPREAD large
/// and the prices small, so a reader scanning the column headed PRICE met "4.61" and spent a moment
/// believing ADA traded at 4.61. It also left the two ranks that column exists for — best bid and
/// worst ask on the same venue at the same instant — with nowhere to appear without a click.
///
/// Depth is the other way round on purpose and the two are not one rule: a sum of both sides is a
/// comparable figure and belongs large, while a price is not a sum of anything.
/// </summary>
public sealed record V2Head(
    IReadOnlyList<V2Part> Big,
    IReadOnlyList<V2Part> Small,
    double? Bar,
    PairColumn? BarColumn);

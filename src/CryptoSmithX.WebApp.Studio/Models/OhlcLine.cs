using System.Globalization;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// The line above one candle panel: the hour a bar covers, its four figures, and the move across
/// it — <c>03:00 UTC  O … H … L … C …  CLOSE VS OPEN +0.41%</c>.
///
/// <b>Why it is a model and not six expressions in the view.</b> Two renderers say this line, the
/// same way two renderers say the statement line: this one, on the server, for the bar the panel
/// rests on, and <c>studio-candles.js</c>, which rewrites the same six fields as the crosshair
/// moves. A line whose arithmetic lives inside a Razor block is a line nothing in CI can read, and
/// the two halves disagreeing is the failure worth spending a file on — the server's slot count is
/// what reserves the room the client's strings then have to fit in, so a drift there is not a
/// wording bug, it is the four keys moving under the pointer.
///
/// <b>THE FIELDS ARE COUNTED, NOT GUESSED.</b> <see cref="FigureGlyphs"/> and
/// <see cref="ChangeGlyphs"/> are the longest strings THIS panel can print, in characters, and
/// <c>studio.css</c> reserves the slots from those counts. The instruction they follow is the one
/// the owner gave when he rejected the first fix for the venue cell: do not size a box to a
/// hypothetical maximum and leave the text free — design the string, then size the slot to it.
/// A hand-picked "nine characters is surely enough" for the change was in the tree for exactly one
/// phase, and it is wrong on the second bar of any hour a memecoin doubles in: <c>+1,204.55%</c> is
/// ten. Counted from the panel's own bars, the slot cannot be overflowed by the data that produced
/// it, and it leaves no hole on a panel whose figures are short.
///
/// <b>Rejected: one count for the whole page.</b> Two venues on this page can quote different
/// assets to different ticks — PEPE at eight decimals beside a book at two — and a slot sized to
/// the longest figure anywhere would print eleven characters of leading space in every panel that
/// is not the widest. The owner accepts visible slack; he did not ask for a page of it.
/// </summary>
/// <param name="When">
/// The hour the four figures are a claim about, as the axis under them spells it. Rule 1 does not
/// stop applying because the figure came out of an hourly rollup: a figure with no date is a claim
/// with no date, and a bar's date is the window it covers.
/// </param>
/// <param name="Change">
/// The move across that hour — the close against that same bar's OWN open, which is the body the
/// reader is looking at. It is never a change against the previous bar, against the session open or
/// against twenty-four hours ago, and the line says so in words beside it
/// (<see cref="ChangeLabel"/>) rather than leaving the reader to assume which convention this page
/// picked. A percentage with no stated base is the class of claim this surface exists not to make.
/// </param>
/// <param name="FigureGlyphs">
/// The width of each of the four figure slots, in characters: the longest of
/// <c>O</c>/<c>H</c>/<c>L</c>/<c>C</c> over every bar this panel holds, and never less than the em
/// dash the gap hours print.
/// </param>
/// <param name="ChangeGlyphs">The same count for the change, over the same bars.</param>
public sealed record OhlcLine(
    string When,
    string Open,
    string High,
    string Low,
    string Close,
    string Change,
    int FigureGlyphs,
    int ChangeGlyphs)
{
    /// <summary>
    /// What the percentage is a percentage OF, printed beside it.
    ///
    /// Every terminal that ships this line prints the change unlabelled — TradingView's legend and
    /// Kraken's header both do — and both get away with it by convention plus a colour. This page
    /// has neither: rule 5 forbids the colour (see the note on the change in <c>studio.css</c>), and
    /// a convention the reader has to already hold is exactly what "the change must say what it is
    /// a change from" rules out. So the base is stated. It costs thirteen characters once per panel
    /// and it is a constant, so it never moves.
    ///
    /// <b>Rejected:</b> <c>CHG</c> — names the quantity and not its base, which is the whole
    /// omission. <c>C−O</c> — shorter and decodable from the four keys to its left, but it asks the
    /// reader to do arithmetic on labels to find out what a figure means, and U+2212 is not in the
    /// DM Mono subsets this page ships (the same trap the △ slot is sized around). <c>Δ</c> — a
    /// symbol for "change" is not a statement of the base either.
    /// </summary>
    public const string ChangeLabel = "CLOSE VS OPEN";

    /// <summary>
    /// Two decimals on the change, against the price columns' own tick.
    ///
    /// A percentage is not a price and does not inherit the venue's step: eight decimals of per-cent
    /// on a PEPE panel would be six digits of noise below anything an hourly bar can support, and
    /// two is what every venue's own change readout prints.
    /// </summary>
    public const int ChangeDecimals = 2;

    /// <summary>
    /// The hour, spelled the way the time axis under it spells one — and in UTC, like every other
    /// instant on this surface. A line in the reader's local zone would be the only figure on the
    /// page meaning something different from its neighbours, and the stamps in the header say Z.
    ///
    /// THE DAY IS PART OF IT, and that is not decoration. A panel holds twenty-five hourly
    /// windows, so the first and the last are the same clock hour twenty-four hours apart: an
    /// "HH:mm UTC" label names both of them identically, and a reader hovering the left edge of
    /// the chart would be told a time that is also true of the right edge. Two bars that are a
    /// day apart must not print the same string.
    ///
    /// Twelve characters in every state a drawn panel can be in — "dd HH:mm UTC" — which is what
    /// lets the slot in <c>studio.css</c> be exact rather than generous. The month is left out
    /// deliberately: twenty-five hours cannot span two days of the same number, so the day alone
    /// disambiguates, and every character bought here is a character the slot must hold.
    /// </summary>
    public static string Hour(DateTime window) =>
        window.ToString("dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>
    /// The move across one bar, or the dash for an hour with no bar.
    ///
    /// There is no guard here for an open of zero, and that is deliberate rather than missed: the
    /// division hands <see cref="Format.SignedPercent"/> an infinity or a NaN, and that method
    /// already answers both with the em dash. One path, one rule — a second copy of "not measured"
    /// written here is a second copy free to drift from rule 8. A flat hour, on the other hand, is
    /// <c>0.00%</c>: the price was observed and it did not move, which is an observation and not an
    /// absence.
    /// </summary>
    public static string ChangeOf(CandleRow? bar) =>
        bar is null
            ? Format.Dash
            : Format.SignedPercent((bar.Close - bar.Open) / bar.Open, ChangeDecimals);

    /// <summary>
    /// The line for one panel, resting on the last CLOSED bar it holds.
    ///
    /// Resting on the last bar rather than on nothing, because this is server-rendered markup and it
    /// has to say something true before any script runs: with scripts off, no pointer, or on paper,
    /// the panel then states four figures and the hour they belong to instead of drawing a shape
    /// with no numbers on it. The client moves the same six fields and puts this state back when the
    /// crosshair leaves.
    /// </summary>
    public static OhlcLine Build(CandleSeries series, int decimals)
    {
        var lastAt = -1;
        for (var i = series.Bars.Count - 1; i >= 0; i--)
        {
            if (series.Bars[i] is not null)
            {
                lastAt = i;
                break;
            }
        }

        var bar = lastAt < 0 ? null : series.Bars[lastAt];

        return new OhlcLine(
            // Windows and Bars are index-aligned by CandleSeries' own contract; the bound is checked
            // anyway because the alternative to a dash here is an exception on a public page.
            When: lastAt < 0 || lastAt >= series.Windows.Count ? Format.Dash : Hour(series.Windows[lastAt]),
            Open: Format.Num(bar?.Open, decimals),
            High: Format.Num(bar?.High, decimals),
            Low: Format.Num(bar?.Low, decimals),
            Close: Format.Num(bar?.Close, decimals),
            Change: ChangeOf(bar),

            // The em dash is appended to both counts rather than used as a fallback for an empty
            // series: a gap hour under the crosshair prints it on a panel that is otherwise full of
            // eleven-character prices, so it is one of the strings the slot has to hold, always.
            FigureGlyphs: series.Bars
                .Where(b => b is not null)
                .SelectMany(b => new[] { b!.Open, b.High, b.Low, b.Close })
                .Select(x => Format.Num(x, decimals).Length)
                .Append(Format.Dash.Length)
                .Max(),
            ChangeGlyphs: series.Bars
                .Select(ChangeOf)
                .Select(s => s.Length)
                .Append(Format.Dash.Length)
                .Max());
    }
}

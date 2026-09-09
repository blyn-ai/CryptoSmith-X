using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// Two rules the review found broken on this sheet, kept as rules rather than as one fix.
///
/// <b>Rule 6 — the acid is three places.</b> --brand had leaked into every pressed state: the cut
/// picker, both switches, the follow toggle, and every bar whatever call wrote it. Up to eight acid
/// slabs on one screen, all of them meaning "selected", which is not what the colour means: rule 5
/// gives acid green to the open-interest call. A page where the accent is everywhere has no accent.
///
/// <b>One source for the tokens.</b> The sheet carried three copies of the variable block while the
/// page also linked ds/styles.css. They agreed on the day they were written, which is the only day
/// four copies of anything agree.
/// </summary>
public sealed class PaletteDisciplineTests
{
    private static string Sheet =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "studio-v2.css"));

    [Fact]
    public void The_sheet_declares_no_tokens_of_its_own()
    {
        var blocks = Regex.Matches(Sheet, @"(?m)^(?::root|\[data-theme=""night""\])\s*\{");

        Assert.True(
            blocks.Count == 0,
            $"{blocks.Count} token block(s) are back in studio-v2.css. Tokens come from ds/tokens/*; "
            + "a copy here is a second source that parts company with the first on the next palette edit.");
    }

    [Fact]
    public void Acid_green_is_used_once_and_it_is_the_line_above_the_header()
    {
        var uses = Regex.Matches(Sheet, @"var\(--brand\)");

        Assert.True(
            uses.Count == 1,
            $"--brand is used {uses.Count} times. Rule 6 gives acid three places on a page — the line "
            + "above the header, the BEST fill (which takes it through --tag-best-bg) and the brand "
            + "mark — and rule 5 gives the colour a meaning: the open-interest call. A pressed "
            + "control is ink.");

        Assert.Matches(@"\.v2-head\{[^}]*border-bottom:2px solid var\(--brand\)", Sheet);
    }

    [Theory]
    [InlineData(".v2-cutpick")]
    [InlineData(".v2-toggle")]
    [InlineData(".v2-switch")]
    public void A_pressed_control_is_ink_and_not_the_accent(string control)
    {
        // Every rule that styles this control's pressed or checked state, whatever the attribute.
        var pressed = Regex.Matches(
            Sheet, Regex.Escape(control) + @"\[aria-(?:pressed|checked)=""true""\][^{]*\{([^}]*)\}");

        Assert.NotEmpty(pressed);

        foreach (Match rule in pressed)
        {
            Assert.DoesNotContain("--brand)", rule.Groups[1].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_bar_takes_the_colour_of_the_call_that_wrote_it()
    {
        // Rule 5 is about meaning, not decoration: a spread bar drawn in the open-interest colour
        // says the open-interest call wrote it, and it did not.
        Assert.Matches(@"\.v2-bar i\{[^}]*background:var\(--bar-ticker\)", Sheet);
        Assert.Matches(@"\.v2-bar\[data-tone=""openinterest""\] i\{background:var\(--bar-oi\)\}", Sheet);
        Assert.Matches(@"\.v2-bar\[data-tone=""depth""\] i\{background:var\(--bar-depth-bid\)\}", Sheet);
    }
}

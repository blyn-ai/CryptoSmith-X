using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// Where the screen's own state is allowed to live on a page the live stream rewrites.
///
/// <b>The defect.</b> The stream replaces bands 1 and 2 whole, about once a second. Every piece of
/// UI state the script had set INSIDE them went with the markup — the opened groups collapsed, the
/// chosen cut snapped back to price, the column count returned to the server's default — and the
/// script put it all back on the next frame. What a reader sees is the page redrawing and losing
/// its layout, once a second, for an update that changed four numbers.
///
/// So the state lives on &lt;body&gt;, outside every live region, and CSS renders it. A push then
/// cannot lose it, and there is nothing to put back.
/// </summary>
public sealed class LiveStateTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void The_script_writes_screen_state_to_the_body_and_not_into_the_bands()
    {
        var js = Read("studio-v2.js");

        Assert.Contains("document.body.setAttribute('data-cut'", js, StringComparison.Ordinal);
        Assert.Contains("document.body.setAttribute('data-cols-'", js, StringComparison.Ordinal);

        // The ways it used to reach inside a replaceable band.
        Assert.DoesNotContain("fields.hidden = !on", js, StringComparison.Ordinal);
        Assert.DoesNotContain("el.hidden = el.getAttribute('data-cut')", js, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.setAttribute('data-cols'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sheet_renders_that_state()
    {
        var css = Read("studio-v2.css");

        Assert.Matches(@"body\[data-cut=""oi""\] \.v2-cuts\[data-cut=""oi""\]\{display:grid\}", css);
        Assert.Matches(@"body\[data-cols-charts=""3""\] \.v2-cuts", css);
    }

    [Fact]
    public void The_books_rule_outranks_the_generic_one_that_would_stack_them()
    {
        // `body[attr] .class[attr]` beats `body[attr] .class`, so the generic "show the selected
        // cut" rule would have won over the books grid and put five books in one column.
        Assert.Matches(
            @"body\[data-cut=""price""\] \.v2-nows--books\[data-cut=""price""\]\{display:grid\}",
            Read("studio-v2.css"));
    }

    [Fact]
    public void A_push_reorders_and_nothing_else()
    {
        var js = Read("studio-v2.js");

        // Re-sorting on every push was the other half of the flicker: an unconditional appendChild
        // for every row, plus a resize telling the chart library to re-measure a width that had not
        // moved. The sort compares first, and the resize belongs to a click.
        var handler = Regex.Match(js, @"document\.addEventListener\('csx-studio-live'[\s\S]{0,400}?\}\);");
        Assert.True(handler.Success);
        Assert.DoesNotContain("dispatchEvent(new Event('resize'))", handler.Value, StringComparison.Ordinal);

        // And it reorders by STYLE. studio-live.js matches children by index, so moving a row in
        // the DOM makes the next push rewrite every row with another venue's data — measured on the
        // live page as a swap every 0.3 s, 718 attribute writes and 407 node insertions in fourteen
        // seconds, which is what a reader calls "the table blinks".
        Assert.Contains("r.style.order = want", js, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.appendChild(r)", js, StringComparison.Ordinal);
        Assert.Contains("if (!quiet) { window.dispatchEvent(new Event('resize')); }", js, StringComparison.Ordinal);
    }
}

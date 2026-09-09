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

        Assert.Contains("document.body.setAttribute('data-open'", js, StringComparison.Ordinal);
        Assert.Contains("document.body.setAttribute('data-cut'", js, StringComparison.Ordinal);
        Assert.Contains("document.body.setAttribute('data-cols-'", js, StringComparison.Ordinal);

        // The three ways it used to reach inside a replaceable band.
        Assert.DoesNotContain("fields.hidden = !on", js, StringComparison.Ordinal);
        Assert.DoesNotContain("el.hidden = el.getAttribute('data-cut')", js, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.setAttribute('data-cols'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sheet_renders_that_state()
    {
        var css = Read("studio-v2.css");

        Assert.Matches(@"body\[data-open~=""price""\] \.v2-cell\[data-group=""price""\] \.v2-fields\[hidden\]\{display:flex\}", css);
        Assert.Matches(@"body\[data-cut=""oi""\] \.v2-cuts\[data-cut=""oi""\]\{display:grid\}", css);
        Assert.Matches(@"body\[data-cols-charts=""3""\] \.v2-cuts", css);
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

        Assert.Contains("var same = rows.every(", js, StringComparison.Ordinal);
        Assert.Contains("if (!quiet) { window.dispatchEvent(new Event('resize')); }", js, StringComparison.Ordinal);
    }
}

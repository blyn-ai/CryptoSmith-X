using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Agent.Tests;

/// <summary>
/// Two rules this screen has now broken once each, kept as rules rather than as two fixes.
///
/// <b>Hidden by attribute, shown by stylesheet.</b> An author declaration setting <c>display</c>
/// beats <c>[hidden]{display:none}</c> from the user-agent sheet at ANY specificity. The internals
/// toggle set the attribute on a <c>.csx-grid</c>, which is <c>display:grid</c>, so pressing it
/// changed the word on the button and nothing else. Nothing errors; the page simply ignores you.
///
/// <b>A stylesheet with no version.</b> Cloudflare caches this domain's static files, so a page can
/// ship new markup and last hour's CSS — measured on the live page as cf-cache-status HIT, age 3213,
/// 179 lines served where the file has 489: no card borders, a browser-default slider, captions
/// glued together. The version in the address is what makes a deploy arrive.
/// </summary>
public sealed class SurfaceTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void Everything_hidden_by_attribute_has_a_rule_that_hides_it()
    {
        var markup = Read("Index.cshtml") + Read("_Field.cshtml");
        var css = Read("agent.css");

        var classes = Regex.Matches(markup, @"<[a-z]+[^>]*\shidden[\s>]", RegexOptions.IgnoreCase)
            .Select(m => Regex.Match(m.Value, @"class=""([^""]*)""").Groups[1].Value)
            .SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(classes);

        foreach (var cls in classes)
        {
            var declares = Regex.IsMatch(
                css, @"\." + Regex.Escape(cls) + @"[^{,]*\{[^}]*display\s*:\s*(?!none)");

            if (!declares)
            {
                continue;
            }

            Assert.True(
                Regex.IsMatch(css, @"\." + Regex.Escape(cls) + @"\[hidden\]"),
                $".{cls} sets display and is hidden by attribute, so the sheet needs .{cls}[hidden] "
                + "— without it the element stays on screen and the control does nothing.");
        }
    }

    [Fact]
    public void Every_static_file_the_page_links_carries_a_version()
    {
        // The design system's own sheet is versioned by its own deploy; ours are not, and ours are
        // the ones that change with every edit to this screen.
        foreach (var (file, pattern) in new[]
        {
            ("_Layout.cshtml", @"agent\.css\?v=\d+"),
            ("Index.cshtml", @"parameters\.js\?v=\d+"),
        })
        {
            Assert.Matches(pattern, Read(file));
        }
    }

    [Fact]
    public void The_save_mechanism_is_the_one_the_server_expects()
    {
        // The design changed how this screen looks. These are the parts that must not move with it:
        // the action, the three hidden fields the controller binds, and the confirm-then-submit
        // path that guards a form which still works with scripting off.
        var markup = Read("Index.cshtml");
        var js = Read("parameters.js");

        Assert.Contains("asp-action=\"Save\"", markup, StringComparison.Ordinal);
        foreach (var hidden in new[] { "strategyProfileId", "strategyRevision", "changeNote" })
        {
            Assert.Contains($"name=\"{hidden}\"", markup, StringComparison.Ordinal);
        }

        Assert.Contains("reportValidity", js, StringComparison.Ordinal);
        Assert.Contains("requestSubmit", js, StringComparison.Ordinal);
        Assert.Contains("dialog.returnValue !== 'save'", js, StringComparison.Ordinal);
    }
}

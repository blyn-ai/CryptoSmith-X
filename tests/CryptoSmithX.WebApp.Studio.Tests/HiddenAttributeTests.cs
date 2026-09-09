using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The one CSS rule this page has now got wrong three times, asserted as a rule instead of as
/// three spot checks.
///
/// <b>What goes wrong.</b> An author declaration setting <c>display</c> beats
/// <c>[hidden] { display: none }</c> from the user-agent sheet ALWAYS — the UA sheet loses to author
/// rules at any specificity — so an element hidden by the attribute stays on screen if its class
/// carries a display of its own. Nothing errors. The page simply shows everything at once.
///
/// It cost the groups (<c>.v2-fields</c>, which never collapsed), and then band 3, where six cuts
/// drew one under another in a band that promises one — the selector above them still worked, it
/// just had nothing to hide.
///
/// So: every class the view hides with the attribute, and which any rule gives a display to, must
/// have a <c>[hidden]</c> pair. Found by reading the markup rather than from a list, because a list
/// is the thing that was forgotten each time.
/// </summary>
public sealed class HiddenAttributeTests
{
    private static readonly string[] Views = ["AssetV2.cshtml", "_V2Table.cshtml", "_V2Now.cshtml"];

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void Every_class_the_view_hides_with_the_attribute_has_a_sheet_rule_to_match()
    {
        // The page AND the partials it is assembled from — band 2's hidden variants live in one
        // of those, and a test that read only the page would have stopped seeing them the day the
        // partial was extracted, silently.
        var view = string.Concat(Views.Select(Read));
        var css = Read("studio-v2.css");

        // Elements carrying `hidden` — literal, or a Razor conditional that may emit it.
        var hiddenTags = Regex.Matches(view, @"<[a-z]+[^>]*\shidden(=""[^""]*"")?[\s/>]", RegexOptions.IgnoreCase);
        Assert.NotEmpty(hiddenTags);

        var classes = hiddenTags
            .Select(m => Regex.Match(m.Value, @"class=""([^""]*)""").Groups[1].Value)
            .SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(c => c.StartsWith("v2-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(classes);

        foreach (var cls in classes)
        {
            // Does any rule give this class a display? Only then is the pair required — a class
            // with no display of its own is hidden by the UA sheet correctly.
            var declares = Regex.IsMatch(
                css, @"(?m)^\." + Regex.Escape(cls) + @"[^{,]*\{[^}]*display\s*:\s*(?!none)");

            if (!declares)
            {
                continue;
            }

            Assert.True(
                Regex.IsMatch(css, @"\." + Regex.Escape(cls) + @"\[hidden\]"),
                $".{cls} sets display and is hidden by attribute in the view, so the sheet needs "
                + $".{cls}[hidden]{{display:none}} — without it the element stays on screen.");
        }
    }

    [Fact]
    public void The_three_that_have_already_broken_are_named()
    {
        // The general assertion above is the guard; these three are the record of what it is for,
        // and they fail loudly if a refactor renames one out from under it.
        var css = Read("studio-v2.css");

        Assert.Matches(@"\.v2-fields\[hidden\]\s*\{\s*display\s*:\s*none", css);
        Assert.Matches(@"\.v2-cuts\[hidden\]", css);
        Assert.Matches(@"\.v2-nows\[hidden\]", css);
    }
}

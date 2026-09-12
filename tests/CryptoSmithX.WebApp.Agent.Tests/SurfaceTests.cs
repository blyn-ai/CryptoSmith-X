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

    [Fact]
    public void A_zero_margin_is_announced_as_a_pause_and_not_left_to_be_noticed()
    {
        // The one value on this screen that means a STATE rather than a size. It is said at the top
        // of the page, in the loudest banner the page has, because an owner who stopped their bot
        // must not have to find the field again to confirm it.
        var page = Read("Index.cshtml");

        Assert.Contains("Model.Profile.PositionMarginUsd == 0m", page, StringComparison.Ordinal);
        Assert.Contains("class=\"paused\" role=\"alert\"", page, StringComparison.Ordinal);
        Assert.Contains("Botas pristabdytas", page, StringComparison.Ordinal);

        // And it says what a pause does NOT do. Silence there would promise a stop that is not one.
        Assert.Contains("Jau atidarytos pozicijos lieka atidarytos", page, StringComparison.Ordinal);

        var css = Read("agent.css");
        Assert.Contains(".p-params .paused{", css, StringComparison.Ordinal);
        Assert.Contains("border-left-width:4px", css, StringComparison.Ordinal);

        // State has ONE colour in this product; the banner borrows it rather than minting a second.
        Assert.Contains("background:var(--tint-hold)", css, StringComparison.Ordinal);

        // THE [hidden] TRAP, caught here for the fifth time in this repository: an author display
        // rule beats the user-agent sheet, so a banner declared display:flex would never hide.
        Assert.Contains(".p-params .paused[hidden]{display:none}", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pause_lights_on_the_number_being_typed_and_not_only_on_the_saved_one()
    {
        // Learning after pressing save that the bot is now paused is learning it too late. The
        // banner is always in the document and the script flips it while the cursor is in the field;
        // the server still sets the first state, so a page without scripts tells the truth about
        // what is stored.
        var page = Read("Index.cshtml");
        Assert.Contains("data-paused", page, StringComparison.Ordinal);
        Assert.Contains("hidden=\"@(Model.Profile.PositionMarginUsd == 0m ? null : \"hidden\")\"", page, StringComparison.Ordinal);

        var js = Read("parameters.js");
        Assert.Contains("[data-paused]", js, StringComparison.Ordinal);
        Assert.Contains("[name=\"positionMarginUsd\"]", js, StringComparison.Ordinal);

        // An empty field is not a zero: it is a field being retyped, and it must not announce a pause.
        Assert.Contains("margin.value !== \'\' && typed === 0", js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_the_profile_lacks_is_a_dash_that_says_why()
    {
        var page = Read("Index.cshtml");
        Assert.Contains("var missing = p.Value is null;", page, StringComparison.Ordinal);

        // No binding name: the form cannot post it, so no save can introduce the key.
        Assert.Contains("missing ? null : $\"Parameters[{d.Id}]\"", page, StringComparison.Ordinal);

        // "Computed rules" is true of a locked field; an absent key computed nothing.
        var field = Read("_Field.cshtml");
        Assert.Contains("Model.Missing ? \"Nėra profilyje\"", field, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lockup_stacks_only_where_it_cannot_fit_in_a_row()
    {
        // Measured on the real page at 375px: the row lockup is 387.7px and the card header gives
        // 269, so the overflow went LEFT (the header is right-aligned) and the magenta plate sat at
        // left:-65.7 — 92px off the edge of the screen. Shrinking type instead was not available:
        // the subline is tracked to the wordmark's exact width, 276px against 275.7, and fitting it
        // beside the plate in 269px would have meant ~7.6px type.
        //
        // The threshold is 500 and not the file's existing 520: the header gives (viewport - 106),
        // so a row stops fitting below 494. Above the threshold nothing changes at all — the same
        // measurement on 1280 still reports flex-direction:row and a 387.7px lockup.
        var css = Read("agent.css");

        Assert.Contains("@media (max-width:500px){", css, StringComparison.Ordinal);
        Assert.Contains(".p-login .lockup{flex-direction:column", css, StringComparison.Ordinal);

        // The desktop rule keeps its own direction, and no media query may restate it.
        Assert.Contains(".p-login .lockup{display:flex;align-items:center;gap:16px}", css, StringComparison.Ordinal);

        // No type is resized on the phone: the wordmark/subline widths are the design's own
        // alignment, and changing one without the other breaks it.
        var mobile = css[css.IndexOf("@media (max-width:500px){", StringComparison.Ordinal)..];
        mobile = mobile[..mobile.IndexOf('}', mobile.IndexOf("lockup", StringComparison.Ordinal))];
        Assert.DoesNotContain("font-size", mobile, StringComparison.Ordinal);
    }
}

using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Admin.Tests;

/// <summary>
/// The landing page is the one surface a stranger reaches first, and its links are the only part of
/// it that can be wrong in a way nobody notices — copy gets read, a 404 gets shrugged at.
///
/// It has already happened. Renaming Arena to Studio moved the public comparison page from /arena to
/// /studio and left two links behind: the hero's "See a pair across venues" pointed at
/// /arena/BTC/USD and the product card at /arena. Both answered 404 on every domain for as long as
/// the rename had been live, because nothing serves that prefix any more and the router falls
/// through to the admin app, which does not know the path.
///
/// No database and no server: the assertion is about what the page says, and that has to be true
/// before anything is started.
/// </summary>
public sealed class LandingLinkTests
{
    private static readonly Regex Href = new("href=\"(?<path>/[^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Prefixes that used to be surfaces and are not any more. A link into one of these is not a
    /// broken guess — it is a rename that did not finish, which is why the list carries the new
    /// address rather than just forbidding the old one.
    /// </summary>
    private static readonly (string Dead, string Live)[] Renamed =
    [
        ("/arena", "/studio"),
    ];

    [Fact]
    public void The_sign_in_page_is_reachable_from_outside()
    {
        // LoginPath used to be "/", which stopped working the moment traefik gave the public root to
        // Studio's storefront: /admin redirected to /?ReturnUrl=/admin, the reader got a marketing
        // page with no form on it, and the console was locked out on both domains — answering 200
        // the entire time, which is why nothing caught it.
        //
        // The path must sit under /admin, because that is the only prefix that still reaches this
        // application, and its route must be registered above the area route, because "admin" is an
        // area name and {area:exists} would read /admin/login as a controller called "login".
        var program = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "Program.cs"));

        Assert.DoesNotContain("o.LoginPath = \"/\";", program, StringComparison.Ordinal);
        Assert.Contains("o.LoginPath = \"/admin/login\";", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("\"admin-login\"", StringComparison.Ordinal)
                < program.IndexOf("\"areas\"", StringComparison.Ordinal),
            "the login route must be registered above the area route");
    }

    [Fact]
    public void The_landing_page_links_to_no_surface_that_was_renamed_away()
    {
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "Landing.cshtml"));
        var links = Href.Matches(page).Select(m => m.Groups["path"].Value).Distinct(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(links);

        foreach (var (dead, live) in Renamed)
        {
            var stale = links
                .Where(l => l.Equals(dead, StringComparison.Ordinal)
                         || l.StartsWith(dead + "/", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                stale.Count == 0,
                $"the landing page still links to {dead}, which nothing serves — use {live}: "
                + string.Join(", ", stale));
        }
    }
}

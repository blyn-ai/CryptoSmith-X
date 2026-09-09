using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// Two designs of the asset page are being judged against each other, so both need an address that
/// says which one it is.
///
/// The first design has always answered at /studio/{asset} and still does — every link anyone kept
/// keeps working, and nothing about that page changed. What it did not have is a name: a button
/// reading "v1" pointing at a bare address says nothing about what it opens, and two buttons
/// standing beside each other have to name the same kind of thing.
/// </summary>
public sealed class DesignAddressTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void Both_designs_have_a_named_route_and_the_bare_address_still_answers()
    {
        var program = Read("Program.cs");

        Assert.Matches(@"""asset-v1""[^;]*?v1/\{baseFamily", program);
        Assert.Matches(@"""asset-v2""[^;]*?v2/\{baseFamily", program);

        // The address the site has always used, unchanged and unredirected: it is in links people
        // have kept and in whatever anyone bookmarked.
        Assert.Matches(@"""asset"",\s*\r?\n\s*@""\{baseFamily", program);
    }

    [Fact]
    public void A_card_opens_the_second_design_and_offers_both()
    {
        var index = Read("Index.cshtml");

        // The card itself, unchanged.
        Assert.Matches(@"a-pair-card"" href=""@href", index);
        Assert.Contains(@"Url.RouteUrl(""asset-v2""", index, StringComparison.Ordinal);
        Assert.Contains(@"Url.RouteUrl(""asset-v1""", index, StringComparison.Ordinal);

        // The two buttons sit BESIDE the card, never inside it: an anchor inside an anchor is
        // invalid markup, and browsers disagree about what to do with it — including making part of
        // the outer one unclickable.
        var card = Regex.Match(index, @"<a class=""a-plain a-pair-card"".*?</a>", RegexOptions.Singleline);
        Assert.True(card.Success);
        Assert.DoesNotContain("<a ", card.Value[3..], StringComparison.Ordinal);
    }
}

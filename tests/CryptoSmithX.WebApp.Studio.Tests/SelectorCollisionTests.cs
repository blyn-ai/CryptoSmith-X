using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// One class name, two meanings, and the table's header stopped being the same grid as its rows.
///
/// <c>.v2-hrow</c> was the header ROW of the table — a grid with the same tracks as every data row,
/// which is the only reason a heading sits over the column it names. Later I gave the same name to
/// the little line INSIDE a header cell that holds the group's name and its caret, and wrote
/// <c>display:flex</c> on it. The second rule won, the header became a flex row of eight items
/// spaced across the table, and every heading stood over the wrong column.
///
/// Nothing errored and no test failed: it is two valid rules about one selector.
/// </summary>
public sealed class SelectorCollisionTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Fact]
    public void The_header_row_and_the_data_rows_are_declared_by_one_rule()
    {
        var css = Read("studio-v2.css");

        // They share a declaration on purpose: two rules with the same tracks written twice is the
        // same defect one edit away.
        Assert.Matches(@"\.v2-calls,\.v2-hrow,\.v2-row\{display:grid;\s*\n?\s*grid-template-columns:", css);

        // And nothing else may set `display` on .v2-hrow, whatever it thinks the name means.
        foreach (Match rule in Regex.Matches(css, @"(?m)^([^{@\r\n]*\.v2-hrow[^{\r\n]*)\{([^}]*)\}"))
        {
            var selector = rule.Groups[1].Value;
            var body = rule.Groups[2].Value;

            if (selector.Contains(".v2-row", StringComparison.Ordinal) || selector.Contains("data-mobile-mode", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("display:", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_header_cell_holds_one_thing_and_needs_no_inner_row()
    {
        // The collision is gone by construction: a header cell is a label (or the button that
        // selects its cut) and nothing else, so there is no second line inside it to name.
        Assert.DoesNotContain("v2-hcell-row", Read("studio-v2.css"), StringComparison.Ordinal);
        Assert.DoesNotContain("v2-hcell-row", Read("_V2Table.cshtml"), StringComparison.Ordinal);
    }
}

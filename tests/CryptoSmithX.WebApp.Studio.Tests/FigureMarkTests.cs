using System.Text.RegularExpressions;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// Two properties of a figure in band 1, both broken once and both invisible to the compiler.
///
/// <b>A figure does not move when you point at it.</b> `.v2-fig:hover` had been written into the
/// same rule as `.v2-figsub` — a selector slip, not a decision — so hovering a figure dropped it
/// from the body size to 9px and repainted it faint. The table changed shape exactly where the
/// reader was looking. The only hover band 1 has is the row's own background.
///
/// <b>A mark is a prefix, not a frame.</b> MAX carried a fill and MIN an outline, so in a column of
/// five rows two figures sat in boxes and three did not: the digits stopped sharing a right edge
/// and the eye read the border instead of the number.
/// </summary>
public sealed class FigureMarkTests
{
    private static string Sheet =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "studio-v2.css"));

    [Fact]
    public void Pointing_at_a_figure_changes_nothing_about_it()
    {
        // Селектор берётся ЦЕЛИКОМ, через переносы строк: ровно так и была написана ошибка —
        // `.v2-fig:hover,` стояло отдельной строкой, а `{` открывалось на следующей, и шаблон,
        // требующий их рядом, эту форму пропускал. Тест, не ловящий исходный дефект, — не тест.
        foreach (Match rule in Regex.Matches(Sheet, @"(?s)(?:^|\}|\*/)([^{}@/]*?)\{"))
        {
            var selector = Regex.Replace(rule.Groups[1].Value, @"\s+", " ").Trim();
            if (!selector.Contains(":hover", StringComparison.Ordinal))
            {
                continue;
            }


            foreach (var owned in new[] { ".v2-fig", ".v2-part", ".v2-cell" })
            {
                Assert.False(
                    Regex.IsMatch(selector, Regex.Escape(owned) + @"[^ ,]*:hover"),
                    $"`{selector}` puts a hover on a figure. Band 1's only hover is the row.");
            }
        }

        Assert.Matches(@"\.v2-row:hover\{background:var\(--surface-sunken\)\}", Sheet);
    }

    [Fact]
    public void A_marked_figure_is_drawn_exactly_like_an_unmarked_one()
    {
        // Nothing may put a box around the span that holds the digits: no fill, no outline, no
        // padding that would shift them off the column's right edge.
        foreach (Match rule in Regex.Matches(Sheet, @"(?m)^\.v2-part--(?:best|worst)(?![ .\w-])[^{]*\{([^}]*)\}"))
        {
            foreach (var boxy in new[] { "background", "outline", "padding", "box-shadow", "border" })
            {
                Assert.DoesNotContain(boxy, rule.Groups[1].Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_mark_keeps_a_column_of_its_own_so_the_digits_line_up()
    {
        // 3ch wide whether the word is there or not: MAX, MIN and the empty slot of an unmarked
        // figure stack under one another, which is the whole reason the digits share an edge.
        Assert.Matches(@"\.v2-part i\{[^}]*width:3ch", Sheet);
        Assert.Matches(@"\.v2-part i\{[^}]*margin-right:2px", Sheet);

        Assert.Matches(@"\.v2-part--best i\{color:var\(--text-heading\)\}", Sheet);
        Assert.Matches(@"\.v2-part--worst i\{color:var\(--text-faint\)\}", Sheet);
    }

    [Fact]
    public void The_quote_rule_hangs_on_the_mark_and_never_on_the_figure()
    {
        // On the span it was the very box edge this change removes. On the mark it is a prefix of a
        // prefix — and an unmarked figure has no mark, so it draws no rule at all.
        Assert.Matches(@"\.v2-part\[data-quote\] i\{box-shadow:inset 3px 0 0 0 [^}]*;padding-left:4px\}", Sheet);
        Assert.DoesNotMatch(@"\.v2-part--(?:best|worst)\[data-quote", Sheet);
    }
}

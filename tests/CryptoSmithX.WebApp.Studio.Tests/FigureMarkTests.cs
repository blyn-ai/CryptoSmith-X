using System.Text.RegularExpressions;
using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

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
/// and the eye read the border instead of the number. The word keeps a ground of its own — the
/// design system's own best/worst chip — but the digits never do.
///
/// <b>The word never lands on the number.</b> The mark used to live in a fixed 3ch box, which was
/// three characters' worth of room and no allowance for the letter-spacing on top of them: an
/// inline-block does not clip, so MIN spilled its last letter onto the first digit.
/// </summary>
public sealed class FigureMarkTests
{
    private static string Sheet =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "studio-v2.css"));

    private static IEnumerable<string> Views =>
        new[] { "_V2Table.cshtml", "_V2Now.cshtml", "_V2Band3Cuts.cshtml" }
            .Select(f => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", f)));

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
        // padding that would shift them off the column's right edge. Правило берётся по КЛАССУ,
        // а не по списку слов: --best/--worst/--good/--bad и любой следующий модификатор ранга
        // попадают сюда сами. Селектор, кончающийся на самом классе, красит .v2-part целиком —
        // то есть цифры; тот же класс с потомком (` i`) красит только слово, и это разрешено.
        foreach (Match rule in Regex.Matches(Sheet, @"(?m)^\.v2-part--[\w-]+\s*(?:,\s*\.v2-part--[\w-]+\s*)*\{([^}]*)\}"))
        {
            foreach (var boxy in new[] { "background", "outline", "padding", "box-shadow", "border" })
            {
                Assert.DoesNotContain(boxy, rule.Groups[1].Value, StringComparison.Ordinal);
            }
        }

        // И у самого числа — тоже ничего: заливка стоит на <i>, span с цифрами её не носит.
        var digits = Regex.Match(Sheet, @"\.v2-part > span\{([^}]*)\}").Groups[1].Value;
        Assert.NotEqual(string.Empty, digits);
        foreach (var boxy in new[] { "background", "outline", "padding", "box-shadow", "border" })
        {
            Assert.DoesNotContain(boxy, digits, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_mark_is_as_wide_as_its_own_word_so_it_never_lands_on_the_digits()
    {
        // Ровно тот дефект: ширина в 3ch на слове из трёх знаков С РАЗРЯДКОЙ. Никакой фиксированной
        // ширины у марки быть не должно — она размечена словом, а разрядка входит в это слово.
        var rule = Regex.Match(Sheet, @"(?s)\.v2-part i\{(.*?)\}").Groups[1].Value;
        Assert.NotEqual(string.Empty, rule);
        Assert.DoesNotMatch(@"(?<!-)\bwidth:", rule);

        // Слово отделено от числа собственным полем — и полем внутри рамки заливки, и отступом
        // снаружи: без обоих заливка касалась бы первой цифры так же, как её касалось слово.
        Assert.Matches(@"padding:1px 3px", rule);
        Assert.Matches(@"margin-right:3px", rule);
    }

    [Fact]
    public void The_mark_carries_the_products_own_best_and_worst_chip()
    {
        // Цвет несёт «хорошо/плохо», слово — «какой край»: два разных факта, и класс у каждого свой.
        // Заливка та же, которой .a-tag--best/--worst красит первую страницу — пара на продукт, а
        // не вторая, похожая на первую.
        Assert.Matches(
            @"\.v2-part--good i,\.v2-part--tight i\{background:var\(--tag-best-bg\);color:var\(--tag-best-ink\)\}",
            Sheet);
        Assert.Matches(
            @"\.v2-part--bad i,\.v2-part--wide i\{background:var\(--tag-worst-bg\);color:var\(--tag-worst-ink\)\}",
            Sheet);

        var page1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "studio.css"));
        foreach (var token in new[] { "--tag-best-bg", "--tag-best-ink", "--tag-worst-bg", "--tag-worst-ink" })
        {
            Assert.Contains(token, page1, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_colour_says_good_or_bad_and_the_word_says_which_end()
    {
        // MIN зелёным на спреде и MAX магентовым на нём же — обе комбинации законны и обязаны
        // быть достижимы: иначе цвет просто дублирует слово и не добавляет ничего.
        var spreadMin = V2Ranks.Mark(V2Field.Spread, Verdict.Best);
        var spreadMax = V2Ranks.Mark(V2Field.Spread, Verdict.Worst);
        Assert.Equal("min", spreadMin);
        Assert.Equal("max", spreadMax);
        Assert.Equal("good", V2Ranks.ToneWord(Verdict.Best));
        Assert.Equal("bad", V2Ranks.ToneWord(Verdict.Worst));

        // А на глубине наоборот: больше — лучше.
        Assert.Equal("max", V2Ranks.Mark(V2Field.Depth25, Verdict.Best));
    }

    [Fact]
    public void In_a_paired_cell_the_marks_face_inward()
    {
        // Верхняя цифра прижата влево, нижняя вправо. Марка-префикс на обеих уводила бы верхнее
        // число от его края, и пара переставала бы читаться как пара.
        Assert.Matches(@"\.v2-fig--pair \.v2-part:first-child\{flex-direction:row-reverse\}", Sheet);
        Assert.Matches(@"\.v2-fig--pair \.v2-part:first-child i\{margin-right:0;margin-left:3px\}", Sheet);
    }

    [Fact]
    public void Nothing_on_this_page_draws_a_one_sided_rule_for_the_quote_asset()
    {
        // Полоска котировки стояла слева у карточки книги, у свечной карточки, у клетки листинга,
        // у строки NOW и у самой марки — шесть мест, одна и та же линия, и в полосе графиков она
        // читалась как край карточки, а не как факт о рынке. Убрана целиком: населённость ранга
        // называет заголовок полосы (#cut-now-rank) и подсказка на самой марке, словами.
        Assert.DoesNotContain("--quote-tint", Sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("data-quote", Sheet, StringComparison.Ordinal);

        // Мёртвого атрибута тоже не остаётся: разметка, которую больше никто не читает, — это
        // приглашение однажды покрасить её обратно, не заметив, что правила уже нет.
        foreach (var view in Views)
        {
            Assert.DoesNotContain("data-quote", view, StringComparison.Ordinal);
        }
    }
}

using System.Text.RegularExpressions;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The hatch says "no bar stood here". It has one job, and it can only do it if it ends exactly
/// where the drawing begins — a hatch that stops short reads as blank paper, which on this page is
/// the one thing it must never read as, and a hatch that runs long puts "no bar here" underneath a
/// bar.
///
/// Both forms got it wrong the same way: the un-held head was measured as a share of the BOX and
/// the drawing was laid out on its own AXIS, and those are two coordinate systems.
///
/// In the candle cards the error even changed sign across panels of the same page — with the first
/// bar in slot 6 the hatch stopped 15px before it, with the first bar in slot 16 it ran 6px under
/// it — because the library reserves a margin before slot 0 and rightOffset empty slots after the
/// last. Nothing but the library's own time scale can answer that, so nothing else computes it.
///
/// In the sparklines the error was small, constant and always short: the hatch stepped by
/// width/count and the line by (width-1)/(count-1).
/// </summary>
public sealed class HatchTests
{
    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", file));

    [Theory]
    [InlineData(0, 24)]
    [InlineData(1, 24)]
    [InlineData(6, 24)]
    [InlineData(17, 24)]
    [InlineData(3, 2)]
    public void The_sparkline_hatch_ends_where_the_line_starts(int leadingGaps, int count)
    {
        // Серия с leadingGaps пустыми часами в начале и настоящими значениями дальше.
        var vals = new List<double?>();
        for (var i = 0; i < count; i++)
        {
            vals.Add(i < leadingGaps ? null : 10 + i);
        }

        var path = Format.SparkPath(vals, 260, 64);
        if (path is null)
        {
            // Меньше двух соседних точек — линии нет, и штриховке нечего помечать.
            return;
        }

        var firstMove = Regex.Match(path, @"M(-?[\d.]+) ");
        Assert.True(firstMove.Success, path);
        var lineStartsAt = double.Parse(firstMove.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        var hatchEndsAt = Format.SparkX(leadingGaps, vals.Count, 260);

        // Одна десятая единицы: путь печатается с одним знаком после запятой, и это весь допуск.
        Assert.True(Math.Abs(hatchEndsAt - lineStartsAt) <= 0.1,
            $"hatch ends at {hatchEndsAt}, line starts at {lineStartsAt}");
    }

    [Fact]
    public void The_sparkline_hatch_and_the_line_share_one_function()
    {
        // Не «оба считают одинаково», а «оба зовут одно»: разойтись двум формулам легче, чем
        // одной, и разойдутся они молча. Что зовёт линия — доказано тестом выше, на значениях;
        // здесь остаётся вторая половина: что то же самое зовёт штриховка.
        var view = Read("AssetV2.cshtml");
        Assert.Contains("Format.SparkX(hatchIndex, vals.Count, 260)", view, StringComparison.Ordinal);
        Assert.DoesNotContain("hatchIndex / vals.Count", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_candle_hatch_is_measured_by_the_axis_and_never_by_the_card()
    {
        // Сервер больше не считает эту ширину вовсе: у него нет и не может быть координат оси.
        var view = Read("_V2Band3Cuts.cshtml");
        Assert.DoesNotContain("hatchPct", view, StringComparison.Ordinal);
        Assert.DoesNotContain("--hatch-w", view, StringComparison.Ordinal);

        // Считает тот, у кого ось: край — центр первого удержанного слота минус половина шага,
        // и шаг спрашивается у той же оси, а не берётся из опций.
        var script = Read("studio-candles.js");
        Assert.Contains("ts.logicalToCoordinate(firstHeld)", script, StringComparison.Ordinal);
        Assert.Contains("ts.logicalToCoordinate(firstHeld - 1)", script, StringComparison.Ordinal);
        Assert.Contains("setProperty('--hatch-w'", script, StringComparison.Ordinal);

        // Ось можно двигать — панели связаны логическим диапазоном, — поэтому штриховка
        // пересчитывается на каждое его изменение, а не один раз при создании.
        var sync = Regex.Match(script, @"(?s)subscribeVisibleLogicalRangeChange\(\(range\) => \{.*?\n    \}\);");
        Assert.True(sync.Success);
        Assert.Contains("paintHatch();", sync.Value, StringComparison.Ordinal);

        // И высота — площадь графика без временной шкалы: под подписями дат «бара нет» не бывает.
        Assert.Contains("ts.height()", script, StringComparison.Ordinal);
        Assert.Contains("setProperty('--hatch-h'", script, StringComparison.Ordinal);

        var sheet = Read("studio-v2.css");
        Assert.Contains("background-size:var(--hatch-w,0) var(--hatch-h,100%)", sheet, StringComparison.Ordinal);
    }
}

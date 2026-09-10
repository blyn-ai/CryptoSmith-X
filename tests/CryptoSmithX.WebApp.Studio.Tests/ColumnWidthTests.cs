using System.Globalization;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The table's tracks: one width per column, chosen for what the column holds, and fixed.
///
/// Content-sized tracks were wrong twice over. A column re-measured itself whenever its text
/// changed, so a hundred and ten ages ticking over re-laid the whole grid once a second — measured
/// on the live page as CLS 0.12 with nothing changing but the clock. And the widest figure a venue
/// happened to print set the width for every other venue, which is a layout that moves when the
/// market does.
/// </summary>
public sealed class ColumnWidthTests
{
    [Fact]
    public void One_string_lays_the_bands_the_headings_and_the_rows()
    {
        // Built once and used by all three, so they cannot drift into three declarations that
        // nearly agree — which is exactly how the header stopped standing over its columns before.
        var tracks = V2Columns.Tracks(true).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(V2Columns.All.Count + 1, tracks.Length);
        Assert.All(tracks, t => Assert.EndsWith("px", t, StringComparison.Ordinal));
    }

    [Fact]
    public void A_column_is_as_wide_as_what_it_holds_and_not_one_width_for_all()
    {
        var widths = V2Columns.All.Select(c => c.Width).ToList();

        Assert.True(widths.Distinct().Count() > 4, "every column is the same width — the table was told nothing about its contents");

        // An interval reads "8 h"; an open interest is nine digits with commas. If those two ever
        // come out equal, the widths have stopped being about content again.
        var interval = V2Columns.All.Single(c => c.Field == V2Field.FundingInterval).Width;
        var openInterest = V2Columns.All.Single(c => c.Field == V2Field.OpenInterest).Width;

        Assert.True(interval < openInterest);
    }

    [Fact]
    public void Every_column_is_wide_enough_for_its_own_heading()
    {
        // A heading wider than its track is a heading that clips, and the reader loses the name of
        // the figure under it. ~5.4px per character at 9px mono with the label's letter-spacing,
        // plus the cell's horizontal padding.
        foreach (var c in V2Columns.All)
        {
            var needed = (int)Math.Ceiling(c.Label.Length * 5.4) + 16 + (c.Cut is null ? 0 : 10);
            Assert.True(c.Width >= needed, $"{c.Label} needs about {needed}px and has {c.Width}px");
        }
    }

    /// <summary>The width of one character of a figure in band 1: the body mono at its own size,
    /// measured on the live page (13 characters of "77,342.000000" came to 109.2px).</summary>
    private const double Char = 8.4;

    /// <summary>What the cell costs around the figure: 6px of padding a side (Prompt 2.1, W-5 —
    /// was 8) and the 1px rule.</summary>
    private const int CellChrome = 13;

    [Theory]
    // Цена печатается шагом площадки, и шаг бывает восьмизначным: «77,355.00000000» у BTC.
    [InlineData(V2Field.Mark, 15)]
    [InlineData(V2Field.Index, 15)]
    [InlineData(V2Field.Last, 13)]
    [InlineData(V2Field.Bid, 13)]
    // Оборот за сутки бывает десятизначным: «1,435,887,165» у ENA.
    [InlineData(V2Field.Turnover24h, 13)]
    // Prompt 2.1, W-3: DEPTH 50BPS folded into DEPTH 25BPS's own second sub-line — no longer a
    // column of its own to measure here.
    public void A_column_holds_the_widest_figure_the_market_prints_and_not_the_one_that_was_open(
        V2Field field, int characters)
    {
        // Ширины выбирались по активу, который был открыт, и на BTC цифры вылезали из клеток на
        // соседние колонки — марк и индекс на 39px. «Что колонка держит» — это про весь рынок.
        var column = V2Columns.All.Single(c => c.Field == field);
        var needed = (int)Math.Ceiling(characters * Char) + CellChrome;

        Assert.True(column.Width >= needed,
            $"{column.Label} holds {characters} characters — about {needed}px — and has {column.Width}px");
    }

    [Fact]
    public void A_column_that_can_print_a_mark_has_room_for_one()
    {
        // Клетка — фиксированный трек, и марка, которая в него не влезла, не переносится и не
        // ужимается: она ВЫЛЕЗАЕТ, а поскольку цифры прижаты вправо — вылезает влево, на соседнюю
        // колонку. Так MAX колонки TURNOVER 24H оказался поверх «4 h» колонки INTERVAL.
        var tracks = V2Columns.Tracks(true).Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();

        for (var i = 0; i < V2Columns.All.Count; i++)
        {
            var c = V2Columns.All[i];
            var track = int.Parse(tracks[i].Replace("px", string.Empty), CultureInfo.InvariantCulture);
            var ranked = V2Ranks.ColumnOf(c.Field) is not null
                || (c.PairedField is { } p && V2Ranks.ColumnOf(p) is not null);

            Assert.Equal(c.Width + (ranked ? V2Columns.MarkSlot : 0), track);
        }

        // И слот не нулевой: правило, которое ничего не прибавляет, — это отсутствие правила,
        // записанное так, что читается как правило.
        Assert.True(V2Columns.MarkSlot >= 24, "a MAX chip is 21px of ground plus its gap");

        // Неранжируемая колонка марки не печатает никогда, и лишних пикселей не носит.
        var interval = V2Columns.All.ToList().FindIndex(c => c.Field == V2Field.FundingInterval);
        Assert.Equal(V2Columns.All[interval].Width + "px", tracks[interval]);
    }
}

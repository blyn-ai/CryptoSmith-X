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
        var tracks = V2Columns.Tracks.Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
}

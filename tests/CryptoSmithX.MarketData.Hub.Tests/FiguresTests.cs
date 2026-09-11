using CryptoSmithX.MarketData.Hub.Ingestion;

namespace CryptoSmithX.MarketData.Hub.Tests;

/// <summary>
/// The rule that replaced a dropped row.
///
/// The snapshot collector used to refuse the WHOLE observation when any one of eight figures was
/// missing, because the columns were NOT NULL and a row had to be written entire. 0030 lifted that,
/// so a missing figure is now a NULL in its own column — but the reason the old guard existed did
/// not go away with it: an absence must never reach the database wearing a number.
///
/// That is the part worth pinning. Postgres accepts NaN and Infinity in a <c>double precision</c>
/// column without complaint, so an unnormalised one would be stored, silently, exactly where the
/// absence belongs — and it would read back as a measurement forever after.
/// </summary>
public sealed class FiguresTests
{
    [Fact]
    public void Null_is_absent() => Assert.True(Figures.Absent(null));

    [Fact]
    public void NaN_is_absent() =>
        // The older spelling: adapters said "not given" this way while a record could not hold a
        // null at all, and a couple still do.
        Assert.True(Figures.Absent(double.NaN));

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Infinity_is_absent(double value) =>
        // What a division by a missing price leaves behind — no more a measurement than the others.
        Assert.True(Figures.Absent(value));

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    [InlineData(77_000)]
    [InlineData(double.Epsilon)]
    public void A_real_number_is_present_and_survives_untouched(double value)
    {
        // ZERO ESPECIALLY. A zero is an observation — an empty side of a book, a market with no
        // open interest right now — and turning it into an absence would destroy the distinction
        // this whole column set exists to keep.
        Assert.False(Figures.Absent(value));
        Assert.Equal(value, Figures.Num(value));
    }

    [Fact]
    public void Every_spelling_of_absence_becomes_one_null()
    {
        Assert.Null(Figures.Num(null));
        Assert.Null(Figures.Num(double.NaN));
        Assert.Null(Figures.Num(double.PositiveInfinity));
        Assert.Null(Figures.Num(double.NegativeInfinity));
    }
}

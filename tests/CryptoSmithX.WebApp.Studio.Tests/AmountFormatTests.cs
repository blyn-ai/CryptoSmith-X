namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// A liquidation volume in base coins is routinely a fraction of one. Printed at whole units, Nado's
/// 0.02015 BTC over the day read "0" — the page's word for an hour measured and found empty.
/// </summary>
public sealed class AmountFormatTests
{
    [Theory]
    [InlineData(0.02015, "0.02015")]
    [InlineData(0.00655, "0.00655")]
    [InlineData(0.123456, "0.1235")]
    [InlineData(1.084, "1")]
    [InlineData(12345.6, "12,346")]
    [InlineData(0d, "0")]
    public void A_fraction_of_a_unit_is_never_printed_as_zero(double value, string expected) =>
        Assert.Equal(expected, Format.Amount(value));

    [Fact]
    public void No_figure_is_still_a_dash() => Assert.Equal(Format.Dash, Format.Amount(null));
}

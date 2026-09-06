using System.Globalization;

namespace CryptoSmithX.WebApp.Admin;

/// <summary>View formatting. A missing value is always an em dash, never an invented number.</summary>
public static class Format
{
    public const string Dash = "—";

    public static string Age(double? seconds)
    {
        if (seconds is null)
        {
            return Dash;
        }

        var s = seconds.Value;
        if (s < 90)
        {
            return $"{s:0} s";
        }

        if (s < 5400)
        {
            return $"{s / 60:0} min";
        }

        return $"{s / 3600:0} h";
    }

    public static string Num(double? value, int decimals = 2) =>
        value is null ? Dash : value.Value.ToString("N" + decimals, CultureInfo.InvariantCulture);

    public static string Utc(DateTime? t) =>
        t is null ? Dash : t.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
}

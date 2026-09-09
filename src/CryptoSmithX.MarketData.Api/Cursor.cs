using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace CryptoSmithX.MarketData.Api;

/// <summary>
/// Where a page stopped, as an opaque string the caller hands back unchanged.
///
/// KEYSET, NOT OFFSET. Every historical endpoint here sorts ascending by (instant, symbol,
/// tiebreak) and resumes with a row-comparison against that same triple, so a page is stable while
/// rows are still arriving behind it — an OFFSET would skip or repeat rows every time a collector
/// wrote into the window mid-pagination, which on these tables is constantly.
///
/// The tiebreak is whatever makes a row unique within its symbol at one instant, and it differs per
/// dataset: nothing at all where the primary key is (instrument, time), the venue's own trade id
/// where several trades share a millisecond, the book sequence for order-book frames. It travels as
/// text so one cursor shape serves all of them.
///
/// Opaque is a contract, not an encoding: base64url of a plain string is easy to read if you try,
/// but nothing about the format is promised, and a caller who parses it gets to keep both pieces.
/// </summary>
public sealed record Cursor(DateTimeOffset At, string Symbol, string Tiebreak)
{
    /// <summary>Round-trips through ISO-8601 with sub-millisecond precision, because these tables
    /// key on instants that regularly collide at millisecond resolution.</summary>
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK";

    public string Encode()
    {
        //  (unit separator) rather than a printable delimiter: symbols are venue-chosen and a
        // comma or colon in one would split the cursor in the wrong place.
        var raw = string.Join(
            '',
            At.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture),
            Symbol,
            Tiebreak);

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>False for anything that is not a cursor this class produced. The caller turns that
    /// into a 400 naming the parameter rather than silently starting from the beginning, which would
    /// hand back a page the caller has already seen and look like duplicated data.</summary>
    public static bool TryDecode(string? encoded, out Cursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(bytes).Split('');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            return false;
        }

        cursor = new Cursor(at.ToUniversalTime(), parts[1], parts[2]);
        return true;
    }
}

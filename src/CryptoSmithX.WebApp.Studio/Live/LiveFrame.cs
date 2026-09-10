using System.Text.Json.Serialization;
using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Live;

/// <summary>
/// One cell's worth of an update, already formatted.
///
/// <b>The client does no arithmetic.</b> Text, sub-line, mark and both ages are produced HERE by
/// <see cref="V2Cells"/>, <see cref="V2Ranks"/> and <see cref="Format"/> — the same code that
/// renders the first paint. A second implementation of "how a number is written" living in
/// JavaScript is the failure the whole live design is arranged to avoid: it is the copy nobody
/// reads and CI cannot see, and the day the two disagree the page is lying in a way no test catches.
/// </summary>
/// <param name="Part">Which half of a paired cell this is — 0 for bid, 1 for ask. Zero everywhere
/// else, because every other column is one figure in one cell.</param>
/// <param name="LiveAge">Age since the VENUE's own observation.</param>
/// <param name="WrittenAge">Age since OUR record of it — what a backtest will see, which is why it
/// is carried beside the live one rather than replaced by it.</param>
/// <param name="Source">"ws" where a socket wrote this figure, "latest" where it is still the
/// database's. A venue with no socket stays honest on a page in live mode.</param>
public sealed record LiveSlot(
    [property: JsonPropertyName("i")] int InstrumentId,
    [property: JsonPropertyName("g")] string Group,
    [property: JsonPropertyName("p")] int Part,
    [property: JsonPropertyName("t")] string Text,
    [property: JsonPropertyName("s")] string? Sub,
    [property: JsonPropertyName("m")] string? Mark,
    [property: JsonPropertyName("tone")] string? Tone,
    [property: JsonPropertyName("a")] string LiveAge,
    [property: JsonPropertyName("w")] string WrittenAge,
    [property: JsonPropertyName("src")] string Source)
{
    /// <summary>What makes this slot the same slot between two frames — its address, not its
    /// content. The diff compares the rest.</summary>
    [JsonIgnore]
    public (int, string, int) Key => (InstrumentId, Group, Part);
}

/// <summary>One update on the wire: only the slots whose content actually moved.</summary>
/// <param name="Signal">"up" or "degraded" — a page whose hub went away must be told, because a
/// frozen figure and a calm market look identical.</param>
public sealed record LiveFrame(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("slots")] IReadOnlyList<LiveSlot> Slots,
    [property: JsonPropertyName("signal")] string Signal);

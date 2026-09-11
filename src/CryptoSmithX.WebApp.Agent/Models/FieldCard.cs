using System.Globalization;

namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>
/// One card of the modeller, as the screen draws it.
///
/// It exists because the two kinds of field on this page — the bot's runtime limits and the
/// strategy profile's parameters — arrive as two different view models and are drawn EXACTLY the
/// same. Two partials that must stay identical is two partials that will not: the header of this
/// page and its rows drifted apart that way once already, in the other app, and nothing errored.
///
/// It carries no behaviour and no new data. Every field here already existed on one of the two
/// sources; this record only says which of them the card reads.
/// </summary>
public sealed record FieldCard(
    string? BindingName,
    string Label,
    string Unit,
    decimal Value,
    decimal Minimum,
    decimal Maximum,
    decimal Step,
    int DecimalPlaces,
    string LowerCaption,
    string HigherCaption,
    string Description,
    string Example,
    string DetailIntro,
    string LowerImpact,
    string HigherImpact,
    string Technical,
    bool Editable,
    string? Tag,
    string? Hint,
    string? Error,
    /// <summary>The profile does not carry this key. Not the same as a locked field: a locked field
    /// holds a figure the profile computed, and this one holds nothing at all — so it prints a dash
    /// and says so, rather than a number nobody measured.</summary>
    bool Missing = false)
{
    /// <summary>The value as the input prints it — the same string the server would validate, so a
    /// reader comparing the field with the sheet sees one number and not two roundings of it.
    /// A dash when the profile does not carry the key: no figure exists to round.</summary>
    public string Display => Missing
        ? "\u2014"
        : Value.ToString("F" + DecimalPlaces, CultureInfo.InvariantCulture);

    public string Invariant(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}

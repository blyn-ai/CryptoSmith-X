using System.Text.RegularExpressions;

namespace CryptoSmithX.Arena;

/// <summary>
/// What may be a family code in an address on this site, in one place.
///
/// The two route templates in <c>Program.cs</c> carry this as a route constraint and argue at length
/// why: an anonymous caller must not be able to turn an arbitrary string into a database lookup, and
/// on the live route it matters more rather than less, because what that endpoint hands out is a
/// connection held open. Both arguments were true and neither was enforced.
///
/// <b>A route constraint guards one address, not one action.</b> The default route is registered
/// first — it has to be, or a two-segment pair route swallows every conventional URL on the site —
/// so <c>/arena/Pairs/Pair?baseFamily=…</c> and <c>/arena/Pairs/Live?baseFamily=…</c> reach the same
/// two actions by name, with the parameters bound from the query string and no constraint anywhere
/// near them. Verified against the running app: a two-hundred-character base family ran the full
/// family-expansion query before returning 404, and the live URL the page itself emitted was the
/// unconstrained form. So the rule moved HERE, where the action is, and the route constraints stay
/// as they are — they still keep the two-segment route from claiming <c>/arena/ds/styles.css</c>,
/// which is a different job and one only a route can do.
///
/// The rule itself is the character class an asset code can contain: 0006 canonicalises base assets
/// and 0024's family codes are written by the admin console, and both are short alphanumerics — BTC,
/// USDT, 1000PEPE. Case is NOT folded, here or downstream: 0024 says the comparison is exact and the
/// case is significant, so this decides what may be looked up and never what it means.
/// </summary>
public static partial class PairAddress
{
    /// <summary>
    /// The pattern, shared with the route templates verbatim. Written as a constant rather than
    /// typed twice, because two copies of a security rule are one rule and one decoration.
    ///
    /// <b>It ends at <c>\z</c> and not at <c>$</c>, and that is the whole of a hole this rule had.</b>
    /// In .NET <c>$</c> matches at the end of the string OR immediately before a single trailing
    /// newline, so <c>"BTC\n"</c> passed a rule whose own character class has no newline in it:
    /// <c>/arena/BTC%0A/USD</c> cleared the route constraint and the action guard, ran the whole
    /// family expansion — two subqueries against <c>asset_family_member</c> and the bitmap scan on
    /// <c>exchange_instrument_pair</c> — and took its own entry in the bounded cache under the key
    /// <c>"pair:BTC\n/USD"</c> before answering 404. Every family code on the site had a second
    /// spelling, so an outsider could double the reachable key space and drive query traffic on
    /// strings all three comments here say are refused. The damage was small because the value is
    /// parameterised and reaches only a 404; the rule being other than the rule as described is not.
    /// <c>\z</c> is the end of the string and nothing else.
    /// </summary>
    public const string Pattern = @"^[A-Za-z0-9][A-Za-z0-9_-]*\z";

    /// <summary>
    /// Sixteen characters, matching the <c>maxlength</c> on both routes. The longest family code the
    /// schema has today is four; sixteen leaves room for an asset nobody has listed yet without
    /// leaving room for a key an outsider composes.
    /// </summary>
    public const int MaxLength = 16;

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Family();

    /// <summary>
    /// Whether this string may be looked up as one half of a pair address. Anything else is a 404
    /// decided before a connection is opened — a claim about the address, not about the market.
    /// </summary>
    public static bool IsFamily(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= MaxLength && Family().IsMatch(code);
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// The STATIC half of a pair's catalogue entry — what belongs in <c>instrument_spec</c>, with every
/// observation cut out first.
///
/// <b>Why this exists at all.</b> Avantis publishes 119 leaf fields per pair in one payload, and
/// they are two different kinds of thing wearing one shape. Leverage bounds, fee tiers, TWAP
/// parameters and OI caps are a SPECIFICATION: they change when the venue changes them, which is
/// rarely. Open interest, funding, the volatility decay and the accumulators are OBSERVATIONS: they
/// change constantly. <c>instrument_spec</c> versions a row whenever <c>spec_hash</c> moves, so
/// handing it the whole payload would mint a new specification version for every instrument on
/// every discovery pass, forever — a version history of nothing, and the largest table in the
/// database within a week.
///
/// <b>The list is measured, not guessed.</b> Two catalogue fetches 45 s apart on 2026-09-11, all
/// 120 pairs compared field by field: exactly ten paths moved, and they are the ten below. The
/// three session fields are included on top of that — they did not move inside a 45 s window and
/// obviously do at a session boundary, which is the whole reason <c>market_open</c> exists.
/// </summary>
internal static class AvantisSpec
{
    /// <summary>Top-level keys that are observations, not specification.</summary>
    private static readonly string[] MovingKeys =
    [
        // Measured as moving, 45 s apart, 120 pairs:
        "openInterest",            // 1-2 pairs moved
        "coinOI",                  // 1-2 pairs moved
        "pairOI",                  // 3 pairs moved
        "fundingRate",             // 3 pairs moved
        "accPerOiLong",            // 10 pairs moved
        "accPerOiShort",           // 10 pairs moved
        "fundingLastUpdateBlock",  // 10 pairs moved
        "decayedVol",              // 19 pairs moved — the fastest of them

        // Did not move inside that window and plainly does over a longer one: liquidity is a
        // function of open interest, which moves. Left in the observation set rather than in the
        // spec, because the cost of being wrong in the other direction is a version per pass.
        "liquidity",
    ];

    /// <summary>Keys under <c>feed.attributes</c> that describe the session rather than the feed.
    /// <c>isOpen</c> is the one <c>market_open</c> records; the two "next" stamps move with it.
    /// <c>schedule</c> STAYS in the spec — it is a fact about the listing and genuinely static, and
    /// keeping it there is not the same as storing a calendar we would have to interpret.</summary>
    private static readonly string[] MovingFeedAttributes = ["isOpen", "nextOpen", "nextClose"];

    /// <summary>
    /// One pair's payload with the observations removed, as the JSON that will be hashed and
    /// versioned. Returns the original text when it cannot be parsed: an unreadable payload is a
    /// reason to record what arrived, not to record nothing.
    /// </summary>
    internal static string StaticJson(JsonElement pair)
    {
        var node = JsonNode.Parse(pair.GetRawText());
        if (node is not JsonObject o)
        {
            return pair.GetRawText();
        }

        foreach (var key in MovingKeys)
        {
            o.Remove(key);
        }

        if (o["feed"] is JsonObject feed && feed["attributes"] is JsonObject attributes)
        {
            foreach (var key in MovingFeedAttributes)
            {
                attributes.Remove(key);
            }
        }

        return o.ToJsonString();
    }
}

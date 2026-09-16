namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// What a segment's own liquidation-sampling quirk is allowed to say on a public page — the same
/// shape <see cref="GapVoice"/> already has for gap causes: a closed, curated vocabulary of house
/// sentences, keyed off a database token rather than off the segment code.
///
/// <b>Why keyed off the token and not the segment.</b> Aster is not the last venue whose
/// <c>!forceOrder@arr</c>-equivalent pushes at most one liquidation per symbol per second — Binance
/// documents the identical rule for its own stream (plans/aster-venue-blueprint.md §3, last
/// paragraph), and that is named there as a follow-up fix rather than done in the same change. When
/// it is fixed, Binance's segment_dataset_capability row gets the SAME <c>sampled_1s_per_symbol</c>
/// value this file already translates, and the label appears with no code change here — a second
/// <c>case "binance-usdm"</c> branch would have had to be remembered and added by hand.
/// </summary>
public static class LiquidationVoice
{
    /// <summary>The caveat a reader needs before trusting a liquidation figure as "the venue's
    /// liquidated volume" — or null when the segment's <c>history_depth</c> capability names no known
    /// quirk (most segments today: either the venue genuinely publishes a tape, or the capability row
    /// has simply never been filled, and this deliberately does not guess which).</summary>
    public static string? Note(string? historyDepth) => historyDepth switch
    {
        "sampled_1s_per_symbol" =>
            "sampled (≤ 1 event/symbol/s) — lower bound",
        _ => null,
    };
}

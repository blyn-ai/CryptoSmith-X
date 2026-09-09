using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// What a break in collection is allowed to say on a public page.
///
/// <b>The defect this exists to close.</b> Band 5 printed <c>cause — detail</c> straight from the
/// row, and <c>detail</c> is whatever the driver threw:
/// <c>NpgsqlException: Failed to connect to 172.22.0.2:5432</c>, <c>40P01 deadlock detected</c>.
/// That is our internal address and our internal port, on a shopfront, in front of anyone. It is
/// also not an answer to the reader's question — they asked what they are missing, not which of our
/// containers could not reach which other one.
///
/// So the cause — which is a closed set, checked by the database since 0017 — becomes one sentence
/// in the page's own voice, and the detail is shown ONLY when it is one of the few things a reader
/// can act on. Everything else is dropped rather than paraphrased: a sentence assembled out of a
/// stack trace reads as an explanation and is not one.
/// </summary>
public static class GapVoice
{
    /// <summary>
    /// The house sentence for each cause the database allows. An unknown cause — one added to the
    /// constraint and not here — says the neutral thing rather than the raw token: this page prints
    /// nothing it has not been written to print.
    /// </summary>
    public static string Say(string cause) => cause switch
    {
        "rate_limited" => "the venue asked us to slow down",
        "timeout" => "the venue did not answer in time",
        "ws_sequence_gap" => "the stream skipped a sequence and was rebuilt",
        "ws_disconnected" => "the stream dropped",
        "resync" => "we resynchronised from scratch",
        "exchange_maintenance" => "the venue was in maintenance",
        "collector_down" => "our collector was not running",
        "error" => "the call failed",
        _ => "collection stopped",
    };

    /// <summary>
    /// The detail, when there is one a reader can use, and null otherwise.
    ///
    /// The whitelist is deliberately narrow: a venue's own error code (which a reader can look up in
    /// that venue's documentation) and an attempt count (which says whether we kept trying). Both
    /// are recognised by SHAPE, and anything that is not exactly one of those shapes is dropped —
    /// a whitelist that matches "contains a number" would pass an IP address on its second reading.
    /// </summary>
    public static string? Detail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var text = detail.Trim();

        // The venue's own code: "code=-1121", "code 10007". Four digits at most and a sign at most,
        // which is every venue code in this system and no address.
        var code = System.Text.RegularExpressions.Regex.Match(
            text, @"(?i)\bcode[ =:]+(-?\d{1,5})\b");
        if (code.Success)
        {
            return $"venue code {code.Groups[1].Value}";
        }

        // How many times we tried: "attempt 3", "retry 5 of 5".
        var attempt = System.Text.RegularExpressions.Regex.Match(
            text, @"(?i)\b(?:attempt|retry)[ =:]+(\d{1,3})\b");
        return attempt.Success ? $"attempt {attempt.Groups[1].Value}" : null;
    }
}

/// <summary>
/// Gaps as the band draws them: one line per (collector, cause), not one line per row.
///
/// Twelve near-identical lines — same venue, same collector, same cause, minutes apart — is a list
/// that has to be read to discover it says one thing. Folded, it says the one thing and how many
/// times: "binance-usdm · candles — the call failed × 9 · 04:00 → 06:12".
/// </summary>
public sealed record GapLine(
    string SegmentCode,
    string Collector,
    string Cause,
    string? Detail,
    int Count,
    DateTime From,
    DateTime? To)
{
    /// <summary>Open first, and open lines are never folded away into a count that hides one: a
    /// break that is still running is the only thing in this band a reader can act on.</summary>
    public bool Open => To is null;

    public static IReadOnlyList<GapLine> Fold(IEnumerable<GapRow> rows)
    {
        return rows
            .GroupBy(g => (g.SegmentCode, g.Collector, g.Cause, Open: g.GapEnd is null))
            .Select(g => new GapLine(
                g.Key.SegmentCode,
                g.Key.Collector,
                g.Key.Cause,
                // The detail is carried only when every row in the fold agrees on it. Two different
                // venue codes folded into one line would attribute one of them to the other's break.
                g.Select(x => GapVoice.Detail(x.Detail)).Distinct().Count() == 1
                    ? GapVoice.Detail(g.First().Detail)
                    : null,
                g.Count(),
                g.Min(x => x.GapStart),
                g.Key.Open ? null : g.Max(x => x.GapEnd)))
            .OrderByDescending(l => l.Open)
            .ThenByDescending(l => l.From)
            .ToList();
    }
}

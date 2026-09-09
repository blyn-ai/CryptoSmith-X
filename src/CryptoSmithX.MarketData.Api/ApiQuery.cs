using System.Globalization;

namespace CryptoSmithX.MarketData.Api;

/// <summary>
/// Request parsing and validation for the historical endpoints, kept pure and away from the handlers
/// so the rules can be tested without a database — the same reason <c>Rollup</c> and <c>Sweep</c> are
/// separable from the loops that call them. Every method returns the parsed value or a message meant
/// to be read by whoever wrote the request, never a bare "invalid".
/// </summary>
public static class ApiQuery
{
    /// <summary>The widest window a single historical request may ask for. Not a performance guess:
    /// the retention on the event tables is what makes a longer window meaningless, and a caller who
    /// asks for a week should be told so rather than handed two days and left to assume the rest was
    /// empty.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(48);

    /// <summary>Instruments per request. A bot watching a book asks for a handful; fifty is already
    /// generous, and the ceiling is what keeps one request from becoming a table scan.</summary>
    public const int MaxSymbols = 50;

    public sealed record Window(DateTimeOffset From, DateTimeOffset To);

    /// <summary>
    /// The comma-separated <c>symbols</c> list, or the legacy singular <c>symbol</c>. Duplicates are
    /// dropped and order is preserved, so a caller repeating a symbol gets one series rather than a
    /// silently doubled one.
    /// </summary>
    public static bool TryParseSymbols(
        string? symbols, string? symbol, out string[] parsed, out string? error)
    {
        parsed = [];
        error = null;

        var raw = !string.IsNullOrWhiteSpace(symbols) ? symbols : symbol;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "symbols is required: a comma-separated list of instrument symbols, "
                + $"at most {MaxSymbols} per request.";
            return false;
        }

        var seen = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seen.Contains(part, StringComparer.Ordinal))
            {
                seen.Add(part);
            }
        }

        if (seen.Count == 0)
        {
            error = "symbols contained no usable entries.";
            return false;
        }

        if (seen.Count > MaxSymbols)
        {
            error = $"symbols asked for {seen.Count} instruments; the limit is {MaxSymbols} per request.";
            return false;
        }

        parsed = [.. seen];
        return true;
    }

    /// <summary>
    /// The <c>from</c>/<c>to</c> pair. Both are required on the historical endpoints and the span
    /// between them is capped: an open-ended range would answer a different question depending on how
    /// much history happened to be retained on the day it was asked.
    /// </summary>
    public static bool TryParseWindow(
        DateTimeOffset? from, DateTimeOffset? to, out Window window, out string? error)
    {
        window = null!;
        error = null;

        if (from is null || to is null)
        {
            error = "from and to are both required, as ISO-8601 instants (for example "
                + "2026-09-09T12:00:00Z).";
            return false;
        }

        if (to <= from)
        {
            error = "to must be later than from.";
            return false;
        }

        var span = to.Value - from.Value;
        if (span > MaxWindow)
        {
            error = $"the window is {Describe(span)}; the maximum is {MaxWindow.TotalHours:0} hours. "
                + "Ask for a shorter range, or page through it with cursor.";
            return false;
        }

        window = new Window(from.Value.ToUniversalTime(), to.Value.ToUniversalTime());
        return true;
    }

    /// <summary>Rows per page. Clamped rather than rejected: a caller asking for more than the
    /// ceiling wants as much as possible, and nextCursor already tells them there is more.</summary>
    public static int ClampLimit(int? limit, int fallback, int max) =>
        limit is null ? fallback : Math.Clamp(limit.Value, 1, max);

    /// <summary>Which of a comma-separated <c>include</c> list the caller asked for. An empty or
    /// absent list means "everything", so the default response is the useful one.</summary>
    public static bool Includes(string? include, string section)
    {
        if (string.IsNullOrWhiteSpace(include))
        {
            return true;
        }

        foreach (var part in include.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, section, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours"
            : $"{span.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes";
}

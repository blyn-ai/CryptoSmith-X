using System.Text.Json;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Records what a historical request asked for and what actually came back (<c>coverage</c>, 0033) —
/// the positive twin of <c>collector_gap</c>: a gap says "the collector fell over and never found
/// out", coverage says "we reached the venue and this is its answer, including when the answer was
/// nothing".
///
/// THE ONE RULE THIS TABLE HAS, and the reason it stayed empty until now: a range older than the
/// venue's real history depth must NEVER be written as covered, or an empty answer to a question the
/// venue could not answer becomes a stored claim that nothing happened then. The intended guard was
/// <c>segment_dataset_capability.history_depth</c> — which is present as a key and NULL for every
/// segment and dataset, so there is nothing to clamp against.
///
/// So the claim is derived from the venue's own answer instead of from a depth nobody has measured:
///
///   * <c>range_from</c> is the OLDEST row the venue actually returned, not the oldest we asked for.
///     A venue that holds thirty days and is asked for ninety answers from day thirty, and this
///     records day thirty — never the eighty-nine days it silently skipped.
///   * <c>range_to</c> is the end we asked for, which the venue did answer up to.
///   * An answer of NOTHING writes NO row at all. "Asked and the venue confirmed empty" and "asked
///     past the end of its history" are indistinguishable without a depth to compare against, and
///     of the two possible mistakes — recording nothing, or recording a false "we checked, it was
///     empty" — only the first is recoverable.
///
/// <c>reason</c> is therefore always NULL here today. The values it can take (throttled, error,
/// timeout, limit_hit, beyond_history) all describe a SHORTFALL against a known expectation, and
/// none of the four venue clients report truncation distinctly from a short answer: a throttle
/// throws (and the pass fails, which is a gap, not coverage), and a row limit looks exactly like a
/// quiet market. Writing a guessed reason would be worse than the null.
/// </summary>
public static class Coverage
{
    /// <summary>
    /// One row per (instrument, dataset, request). Call INSIDE the caller's own transaction so the
    /// coverage claim and the data it describes land together or not at all — a coverage row whose
    /// data was rolled back is exactly the false claim above.
    /// </summary>
    /// <param name="observed">When each returned item is dated, in any order. Empty means nothing
    /// came back and nothing is written.</param>
    public static async Task WriteAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string datasetCode,
        DateTimeOffset requestedTo,
        DateTimeOffset requestedAt,
        IReadOnlyList<(int InstrumentId, DateTimeOffset Observed)> observed,
        CancellationToken ct)
    {
        if (observed.Count == 0)
        {
            return;
        }

        var oldest = new Dictionary<int, DateTimeOffset>();
        var counted = new Dictionary<int, int>();
        foreach (var (id, at) in observed)
        {
            if (!oldest.TryGetValue(id, out var current) || at < current)
            {
                oldest[id] = at;
            }

            counted[id] = counted.GetValueOrDefault(id) + 1;
        }

        var rows = new List<CoverageRow>(oldest.Count);
        foreach (var (id, from) in oldest)
        {
            // The table's CHECK requires range_to > range_from. A single item dated at or after the
            // requested end (a bar that closed on the boundary) would otherwise fail the insert;
            // the request's own end is the honest upper bound in that case.
            if (from >= requestedTo)
            {
                continue;
            }

            rows.Add(new CoverageRow(id, datasetCode, from, requestedTo, counted[id], requestedAt));
        }

        if (rows.Count == 0)
        {
            return;
        }

        await using var cmd = new NpgsqlCommand(
            """
            insert into coverage (
                exchange_instrument_id, dataset_code, range_from, range_to, returned, requested_at)
            select exchange_instrument_id, dataset_code, range_from, range_to, returned, requested_at
              from jsonb_to_recordset(@rows) as x(
                   exchange_instrument_id integer, dataset_code text,
                   range_from timestamptz, range_to timestamptz,
                   returned integer, requested_at timestamptz)
            """,
            conn, tx);

        var json = cmd.Parameters.Add("rows", NpgsqlTypes.NpgsqlDbType.Jsonb);
        json.Value = JsonSerializer.Serialize(rows);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record CoverageRow(
        int exchange_instrument_id,
        string dataset_code,
        DateTimeOffset range_from,
        DateTimeOffset range_to,
        int returned,
        DateTimeOffset requested_at);
}

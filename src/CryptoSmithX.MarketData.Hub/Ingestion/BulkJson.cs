using System.Text.Json;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Runs one <c>jsonb_to_recordset</c> insert in bounded batches.
///
/// A pass's whole result in a single statement is the obvious shape and it does not survive contact
/// with the bigger venues: Kraken's open-interest pass is 275 symbols × 720 hourly buckets ≈ 198k
/// rows, and one statement carrying that much JSON hit Npgsql's command timeout and failed the pass
/// outright — found live on test, where Binance's 22k-row pass had gone through fine and hid the
/// problem. Chunking makes the writer's cost a function of the batch size rather than of how much
/// history the dataset happens to be configured to ask for.
///
/// Each chunk is its own statement, so a caller wanting all-or-nothing passes a transaction and
/// gets it; the chunking itself does not weaken that.
/// </summary>
public static class BulkJson
{
    /// <summary>
    /// Rows per statement. 5,000 keeps the serialised document in the low megabytes for every row
    /// shape here — including <c>book_topn</c>, whose rows carry four arrays each — while still
    /// making a large pass a handful of round trips rather than hundreds.
    /// </summary>
    private const int ChunkSize = 5_000;

    /// <summary>Returns the total number of rows the server reported affected.</summary>
    public static async Task<int> WriteAsync<T>(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string sql,
        IReadOnlyList<T> rows,
        CancellationToken ct)
    {
        var affected = 0;
        for (var offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            var chunk = rows.Skip(offset).Take(ChunkSize).ToList();

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            var json = cmd.Parameters.Add("rows", NpgsqlTypes.NpgsqlDbType.Jsonb);
            json.Value = JsonSerializer.Serialize(chunk);
            affected += await cmd.ExecuteNonQueryAsync(ct);
        }

        return affected;
    }
}

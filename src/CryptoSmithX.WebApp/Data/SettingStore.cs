using System.Data.Common;
using System.Globalization;
using CryptoSmithX.WebApp.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// The global market-data settings table. Each value is validated against its declared kind before
/// it is written, so a typo becomes an alert on the page rather than a Hub that throws when it next
/// reads the value. Every write stamps who did it.
/// </summary>
public static class SettingStore
{
    public static async Task<IReadOnlyList<SettingRow>> ListAsync(DbConnection conn, CancellationToken ct) =>
        (await conn.QueryAsync<SettingRow>(new CommandDefinition(
            """
            select key         as "Key",
                   value       as "Value",
                   kind        as "Kind",
                   description as "Description",
                   updated_at  as "UpdatedAt",
                   updated_by  as "UpdatedBy"
              from setting
             order by key
            """,
            cancellationToken: ct))).ToList();

    /// <summary>Validates against the setting's kind and writes it with audit. Returns an error
    /// message, or null on success.</summary>
    public static async Task<string?> UpdateAsync(
        DbConnection conn, string key, string value, string? updatedBy, CancellationToken ct)
    {
        var kind = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "select kind from setting where key = @key", new { key }, cancellationToken: ct));
        if (kind is null)
        {
            return "Unknown setting.";
        }

        value = (value ?? "").Trim();
        var error = Validate(kind, value);
        if (error is not null)
        {
            return error;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "update setting set value = @value, updated_by = @updatedBy, updated_at = now() where key = @key",
            new { key, value, updatedBy },
            cancellationToken: ct));
        return null;
    }

    private static string? Validate(string kind, string value) => kind switch
    {
        "int" => IsPositiveInt(value) ? null : "Must be a positive whole number.",
        "int_list" => IsIntList(value) ? null : "Must be positive whole numbers separated by commas.",
        "text" => value.Length > 0 ? null : "Cannot be empty.",
        _ => "Unknown kind.",
    };

    private static bool IsPositiveInt(string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0;

    private static bool IsIntList(string v)
    {
        var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0
            && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0);
    }
}

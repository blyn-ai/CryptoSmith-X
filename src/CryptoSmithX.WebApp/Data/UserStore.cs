using System.Data.Common;
using Dapper;

namespace CryptoSmithX.WebApp.Data;

/// <summary>
/// Human sign-in accounts, now in the database (table <c>webapp_user</c>). The password is compared
/// in clear text for now — an interim choice for an internal tool, to be hashed again later.
/// </summary>
public static class UserStore
{
    /// <summary>The account for a username, or null. No password check here — the caller compares.</summary>
    public static async Task<UserRow?> FindAsync(DbConnection conn, string username, CancellationToken ct)
    {
        return await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            """
            select username    as "Username",
                   password    as "Password",
                   role        as "Role",
                   tenant_code as "TenantCode"
              from webapp_user
             where username = @username
            """,
            new { username },
            cancellationToken: ct));
    }

    public sealed record UserRow(string Username, string? Password, string Role, string? TenantCode);
}

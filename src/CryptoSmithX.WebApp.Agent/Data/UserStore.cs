using System.Data.Common;
using Dapper;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// Sign-in accounts, read from the same <c>webapp_user</c> table the admin console owns — the
/// owner's instruction was "логины брать из admin", so this is that table and not a copy of it.
///
/// The password is still stored and compared in CLEAR TEXT, which is what the column holds today;
/// this class does not make that worse and does not pretend otherwise. Hashing is one migration and
/// one comparison away and belongs to both applications at once, not to this one alone.
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

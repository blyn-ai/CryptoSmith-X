namespace CryptoSmithX.WebApp.Auth;

/// <summary>
/// The one authorization rule worth guarding by itself: a "user" only ever sees their own tenant's
/// bots. The tenant is taken from the signed-in claim and threaded into every SQL filter, so an
/// empty or missing claim must be refused rather than silently matching every row.
/// </summary>
public static class TenantScope
{
    /// <summary>
    /// Returns the tenant code to filter by, or throws if it is empty — an account with no tenant can
    /// access no bots, which is not the same as "all bots".
    /// </summary>
    public static string Require(string? tenantCode)
    {
        if (string.IsNullOrWhiteSpace(tenantCode))
        {
            throw new InvalidOperationException("This account is not bound to a tenant and cannot access any bots.");
        }

        return tenantCode.Trim();
    }
}

namespace CryptoSmithX.WebApp.Options;

/// <summary>
/// The WebApp's configuration surface. Human users are hardcoded here, not in the database — the
/// cookie scheme is the seam where an OIDC provider (authentik) can be added later without touching
/// this. Every value is overridable by environment (<c>WebApp__Users__0__PasswordHash=...</c>).
/// </summary>
public sealed class WebAppOptions
{
    public const string SectionName = "WebApp";

    public List<UserOptions> Users { get; init; } = [];
}

public sealed class UserOptions
{
    public string Username { get; init; } = "";

    /// <summary>PBKDF2-SHA256 hash in the form <c>{iterations}.{salt-b64}.{hash-b64}</c>.</summary>
    public string PasswordHash { get; init; } = "";

    /// <summary>"admin" or "user".</summary>
    public string Role { get; init; } = "user";

    /// <summary>The tenant a "user" is bound to. Empty for "admin".</summary>
    public string TenantCode { get; init; } = "";
}

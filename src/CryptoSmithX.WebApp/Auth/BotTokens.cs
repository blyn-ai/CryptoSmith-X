using System.Security.Cryptography;
using System.Text;

namespace CryptoSmithX.WebApp.Auth;

/// <summary>
/// Opaque bot tokens. A token is 32 random bytes, base64url-encoded, shown to the operator exactly
/// once at creation and never stored. What is stored is <see cref="Hash"/> of it — a lookup key, not
/// a password, so a plain SHA-256 (no salt) is right: it has to be reproducible from the token alone.
/// </summary>
public static class BotTokens
{
    private const int TokenBytes = 32;

    /// <summary>A fresh token, base64url without padding.</summary>
    public static string Generate()
    {
        var raw = RandomNumberGenerator.GetBytes(TokenBytes);
        return Base64Url(raw);
    }

    /// <summary>Lowercase hex of the SHA-256 of the token, as stored in <c>bot.token_hash</c>.</summary>
    public static string Hash(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(digest);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

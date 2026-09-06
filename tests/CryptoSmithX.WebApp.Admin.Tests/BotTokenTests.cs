using System.Security.Cryptography;
using System.Text;
using CryptoSmithX.WebApp.Admin.Auth;

namespace CryptoSmithX.WebApp.Admin.Tests;

public sealed class BotTokenTests
{
    [Fact]
    public void A_generated_token_hashes_to_the_stored_sha256()
    {
        var token = BotTokens.Generate();
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        Assert.Equal(expected, BotTokens.Hash(token));
    }

    [Fact]
    public void The_hash_is_stable_and_distinguishes_tokens()
    {
        var token = BotTokens.Generate();
        Assert.Equal(BotTokens.Hash(token), BotTokens.Hash(token));
        Assert.NotEqual(BotTokens.Hash(token), BotTokens.Hash(BotTokens.Generate()));
    }

    [Fact]
    public void A_token_is_base64url_carrying_32_bytes_of_entropy()
    {
        var token = BotTokens.Generate();

        // base64url: no padding, no + or /.
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);

        // 32 bytes → 43 base64 chars without padding.
        Assert.Equal(43, token.Length);

        var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(32, bytes.Length);
    }

    [Fact]
    public void Two_tokens_are_not_equal()
    {
        Assert.NotEqual(BotTokens.Generate(), BotTokens.Generate());
    }
}

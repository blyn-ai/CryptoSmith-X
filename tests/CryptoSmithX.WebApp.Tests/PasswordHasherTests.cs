using CryptoSmithX.WebApp.Auth;

namespace CryptoSmithX.WebApp.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void A_hash_verifies_against_its_own_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void The_wrong_password_does_not_verify()
    {
        var hash = PasswordHasher.Hash("s3cret");
        Assert.False(PasswordHasher.Verify("S3cret", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        // A fresh salt per hash: two hashes of one password must differ, yet both verify.
        var a = PasswordHasher.Hash("same");
        var b = PasswordHasher.Hash("same");
        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("same", a));
        Assert.True(PasswordHasher.Verify("same", b));
    }

    [Fact]
    public void The_stored_form_is_iterations_salt_hash()
    {
        var parts = PasswordHasher.Hash("x").Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("100000", parts[0]);
        Assert.NotEmpty(Convert.FromBase64String(parts[1]));
        Assert.NotEmpty(Convert.FromBase64String(parts[2]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("100000.only-two-parts")]
    [InlineData("abc.def.ghi")]                 // non-base64 fields
    [InlineData("0.c2FsdA==.aGFzaA==")]         // zero iterations
    public void A_tampered_or_malformed_hash_string_fails_rather_than_throws(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }
}

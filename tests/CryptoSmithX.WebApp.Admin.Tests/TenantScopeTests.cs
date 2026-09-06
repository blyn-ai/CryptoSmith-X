using CryptoSmithX.WebApp.Admin.Auth;

namespace CryptoSmithX.WebApp.Admin.Tests;

public sealed class TenantScopeTests
{
    [Fact]
    public void A_real_tenant_passes_through_trimmed()
    {
        Assert.Equal("DENIS", TenantScope.Require("DENIS"));
        Assert.Equal("DENIS", TenantScope.Require("  DENIS  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_tenant_is_refused_so_the_filter_never_matches_everything(string? tenant)
    {
        // An account with no tenant can access no bots — which is not the same as "all bots".
        Assert.Throws<InvalidOperationException>(() => TenantScope.Require(tenant));
    }
}

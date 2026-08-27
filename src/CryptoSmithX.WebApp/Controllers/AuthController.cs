using System.Security.Claims;
using CryptoSmithX.WebApp.Auth;
using CryptoSmithX.WebApp.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CryptoSmithX.WebApp.Controllers;

/// <summary>Cookie sign-in and sign-out. Users are the hardcoded ones from configuration.</summary>
[AllowAnonymous]
public sealed class AuthController : Controller
{
    private readonly WebAppOptions _options;

    public AuthController(IOptions<WebAppOptions> options) => _options = options.Value;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? username, string? password)
    {
        var user = _options.Users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.Ordinal));

        if (user is null || !PasswordHasher.Verify(password ?? "", user.PasswordHash))
        {
            TempData["LoginFailed"] = true;
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new("tenantCode", user.TenantCode ?? ""),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Redirect(user.Role == "admin" ? "/Admin" : "/My");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(HomeController.Index), "Home");
    }
}

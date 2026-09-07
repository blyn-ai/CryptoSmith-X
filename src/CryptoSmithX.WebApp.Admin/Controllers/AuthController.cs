using System.Security.Claims;
using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Admin.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Controllers;

/// <summary>Cookie sign-in and sign-out. Users live in the database (<c>webapp_user</c>).</summary>
[AllowAnonymous]
public sealed class AuthController : Controller
{
    private readonly Db _db;

    public AuthController(Db db) => _db = db;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? username, string? password, bool rememberMe, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var user = await UserStore.FindAsync(conn, username ?? "", ct);

        // Plaintext comparison for now; constant-time is moot without hashing. A seeded account has
        // no password (null) and can never sign in until an operator sets one directly in the database.
        if (user is null
            || string.IsNullOrEmpty(user.Password)
            || !string.Equals(user.Password, password, StringComparison.Ordinal))
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
        // Unchecked box = session cookie, gone when the browser closes; checked = the cookie
        // persists for the ExpireTimeSpan configured in Program.cs (7 days, sliding).
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = rememberMe });

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

using System.Security.Claims;
using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Agent.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Agent.Controllers;

/// <summary>The sign-in screen, which is this application's home page, and the two posts it makes.</summary>
[AllowAnonymous]
public sealed class HomeController : Controller
{
    private readonly Db _db;

    public HomeController(Db db) => _db = db;

    public IActionResult Index()
    {
        // Already signed in: there is nothing on this page for you. Straight to the parameters,
        // which is where a sign-in ends anyway.
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction(nameof(ParametersController.Index), "Parameters");
        }

        return View();
    }

    [HttpGet]
    public IActionResult Login() => RedirectToAction(nameof(Index));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? username, string? password, bool rememberMe, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var user = await UserStore.FindAsync(conn, username ?? "", ct);

        // Clear-text comparison, because that is what the column holds; constant-time is moot
        // without hashing. A seeded account has no password (null) and can never sign in until an
        // operator sets one directly in the database — which is deliberate, and is why an empty
        // posted password must not match it.
        if (user is null
            || string.IsNullOrEmpty(user.Password)
            || !string.Equals(user.Password, password, StringComparison.Ordinal))
        {
            // The same message for an unknown account and for a wrong password, on purpose: two
            // different answers turn this form into a way to ask which usernames exist.
            TempData["LoginFailed"] = true;
            return RedirectToAction(nameof(Index));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new("tenantCode", user.TenantCode ?? ""),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        // Unchecked box = a session cookie, gone when the browser closes; checked = it persists for
        // the ExpireTimeSpan configured in Program.cs (7 days, sliding).
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = rememberMe });

        return RedirectToAction(nameof(ParametersController.Index), "Parameters");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        // Back to the root — which for this application is the sign-in page, because the sign-in
        // page IS the home page. Not the site's storefront: signing out and signing back in is
        // one action interrupted, and it should end where it can be resumed rather than two
        // clicks away from it.
        return RedirectToAction(nameof(Index));
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Controllers;

/// <summary>The public landing and the sign-in page. Anonymous; a signed-in visitor is sent to
/// their area from either one.</summary>
[AllowAnonymous]
public sealed class HomeController : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect(User.IsInRole("admin") ? "/Admin" : "/My");
        }

        return View();
    }

    /// <summary>Sign-in lives on its own page now — the landing's first screen is the product,
    /// not a form. <see cref="Controllers.AuthController.Login"/> posts back here on failure.</summary>
    [HttpGet("/login")]
    public IActionResult Login()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect(User.IsInRole("admin") ? "/Admin" : "/My");
        }

        ViewData["LoginFailed"] = TempData["LoginFailed"] is true;
        return View();
    }
}

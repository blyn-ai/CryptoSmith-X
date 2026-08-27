using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Controllers;

/// <summary>The public sign-in page. Anonymous; a signed-in visitor is sent to their area.</summary>
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

        ViewData["LoginFailed"] = TempData["LoginFailed"] is true;
        return View();
    }
}

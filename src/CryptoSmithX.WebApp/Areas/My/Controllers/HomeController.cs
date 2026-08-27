using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.My.Controllers;

[Area("My")]
[Authorize(Roles = "user")]
public sealed class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Bots");
}

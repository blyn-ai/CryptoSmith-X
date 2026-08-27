using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Bots");
}

using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Studio.Controllers;

/// <summary>
/// The storefront — what a visitor arriving at the bare domain sees.
///
/// It holds no data of its own and takes no dependency: every figure on the page is fetched by the
/// browser, from the coverage endpoint and from the bot API, and until those land the page shows
/// dashes. That is deliberate and it is the same rule the rest of the site keeps — a dash is not a
/// zero — so rendering it server-side with figures baked in at request time would be the one place
/// the site quietly broke its own argument.
/// </summary>
public sealed class HomeController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}

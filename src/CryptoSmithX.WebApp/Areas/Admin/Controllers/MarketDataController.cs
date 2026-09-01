using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// The old read-only market-data console is gone: its collector view is covered by the Dashboard and
/// Exchanges/Details, and its instrument list by /Admin/Instruments. The controller stays only to
/// redirect any bookmark or link that still points here.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class MarketDataController : Controller
{
    [HttpGet]
    public IActionResult Index() => RedirectToActionPermanent("Index", "Instruments", new { area = "Admin" });
}

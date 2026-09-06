using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Admin.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Areas.Admin.Controllers;

/// <summary>The admin home: a status dashboard that answers "is anything wrong right now".</summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class HomeController : Controller
{
    private readonly Db _db;

    public HomeController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var dash = await DashboardStore.LoadAsync(conn, ct);
        return View(dash);
    }
}

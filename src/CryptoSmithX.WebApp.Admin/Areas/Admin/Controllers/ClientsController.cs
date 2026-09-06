using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Admin.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Areas.Admin.Controllers;

/// <summary>
/// Clients, derived from tenant + bot. The consent surface — which data a client chooses to share —
/// is the point of these screens, but it needs tables that do not exist yet; the views mark those
/// parts as sample and carry the migration request rather than inventing a consent store.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class ClientsController : Controller
{
    private readonly Db _db;

    public ClientsController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return View(await ClientStore.ListAsync(conn, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Details(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var client = await ClientStore.GetAsync(conn, id, ct);
        return client is null ? NotFound() : View(client);
    }
}

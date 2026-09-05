using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// The catalogue of data kinds: defaults live here, and only exchanges that departed from a
/// default show a row — this page never edits policy, that is the Edit feed dialog on the exchange
/// page (<see cref="ExchangesController.Feed"/>).
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class DatasetsController : Controller
{
    private readonly Db _db;

    public DatasetsController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return View(await DatasetStore.ListAsync(conn, ct));
    }
}

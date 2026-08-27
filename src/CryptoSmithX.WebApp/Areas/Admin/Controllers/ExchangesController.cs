using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// The integrations dashboard. Lifecycle (status, name, description) is edited here; connection
/// health and every number on screen are derived from collector_status and exchange_instrument
/// on each render — the exchange row never stores an observation.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class ExchangesController : Controller
{
    private readonly Db _db;

    public ExchangesController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var exchanges = await ExchangeStore.ListAsync(conn, ct);
        return View(exchanges);
    }

    [HttpGet]
    public async Task<IActionResult> Details(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var details = await ExchangeStore.GetAsync(conn, id, ct);
        return details is null ? NotFound() : View(details);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        string id, string? name, string? status, string? description, CancellationToken ct)
    {
        name = name?.Trim() ?? "";
        status = status?.Trim() ?? "";
        if (name.Length == 0)
        {
            TempData["Error"] = "Name is required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await using var conn = await _db.OpenAsync(ct);
        var saved = await ExchangeStore.SaveSettingsAsync(conn, id, name, status, description?.Trim(), ct);
        if (!saved)
        {
            TempData["Error"] = "Unknown exchange or invalid status.";
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["Saved"] = "Saved.";
        return RedirectToAction(nameof(Details), new { id });
    }
}

using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// Every instrument across every exchange, filtered and paged, and the per-instrument detail page —
/// the deepest view in the console: snapshot, price, microstructure, funding and coverage, plus the
/// live collect toggle that decides whether the Hub keeps gathering this listing.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class InstrumentsController : Controller
{
    private const int PageSize = 50;

    private readonly Db _db;

    public InstrumentsController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(
        string? exchange, string? status, bool onlyTrading, string? q, string? sort, int page, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var model = await InstrumentStore.ListAsync(
            conn, exchange, status, onlyTrading, q, sort ?? "symbol", Math.Max(1, page), PageSize, ct);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, int tf, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var details = await InstrumentStore.GetAsync(conn, id, tf == 0 ? 1 : tf, ct);
        return details is null ? NotFound() : View(details);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCollect(int id, bool collect, string? note, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        if (await InstrumentStore.SaveCollectAsync(conn, id, collect, note, User.Identity?.Name, ct))
        {
            TempData["Saved"] = collect ? "Collection enabled." : "Collection disabled.";
        }
        else
        {
            TempData["Error"] = "Unknown instrument.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }
}

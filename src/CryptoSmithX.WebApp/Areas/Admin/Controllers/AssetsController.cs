using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// Canonical assets: the list is a cross-exchange roll-up, the detail is where an operator compares
/// an asset's listings side by side and fixes the alias mapping that discovery resolves against.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class AssetsController : Controller
{
    private readonly Db _db;

    public AssetsController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        ViewData["Search"] = q;
        return View(await AssetStore.ListAsync(conn, q, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Details(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var details = await AssetStore.GetAsync(conn, id, ct);
        return details is null ? NotFound() : View(details);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string id, string? name, string? note, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        if (await AssetStore.UpdateAsync(conn, id, name, note, User.Identity?.Name, ct))
        {
            TempData["Saved"] = "Saved.";
        }
        else
        {
            TempData["Error"] = "Unknown asset.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAlias(
        string id, string? exchangeCode, string alias, string multiplier, string? note, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var error = await AssetStore.AddAliasAsync(conn, id, exchangeCode, alias, multiplier, note, ct);
        if (error is null)
        {
            TempData["Saved"] = "Alias saved. Discovery re-binds instruments on its next pass.";
        }
        else
        {
            TempData["Error"] = error;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAlias(string id, string? exchangeCode, string alias, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await AssetStore.DeleteAliasAsync(conn, exchangeCode, alias, ct);
        TempData["Saved"] = "Alias removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var error = await AssetStore.DeleteAssetAsync(conn, id, ct);
        if (error is null)
        {
            TempData["Saved"] = "Asset deleted.";
            return RedirectToAction(nameof(Index));
        }

        TempData["Error"] = error;
        return RedirectToAction(nameof(Details), new { id });
    }
}

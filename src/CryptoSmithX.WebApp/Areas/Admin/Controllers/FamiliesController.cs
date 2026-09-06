using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// Asset families: the display-only fold from 0024, sitting one level above the canonical asset and
/// edited beside the aliases that produce it. Same shape as <see cref="AssetsController"/> — a
/// searchable list, a detail page that owns the registry entry, and writes that come back as an
/// alert rather than an exception.
///
/// Nothing on this page changes stored market data. Editing a family changes which rows a pair
/// draws together; it never changes what a venue's quote is.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class FamiliesController : Controller
{
    private readonly Db _db;

    public FamiliesController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        ViewData["Search"] = q;
        return View(await AssetFamilyStore.ListAsync(conn, q, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Details(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var details = await AssetFamilyStore.GetAsync(conn, id, ct);
        return details is null ? NotFound() : View(details);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string code, string? name, string? note, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var error = await AssetFamilyStore.CreateAsync(conn, code, name, note, User.Identity?.Name, ct);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        TempData["Saved"] = "Family created. It folds nothing until an asset is added to it.";
        return RedirectToAction(nameof(Details), new { id = (code ?? "").Trim() });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string id, string? name, string? note, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        if (await AssetFamilyStore.UpdateAsync(conn, id, name, note, User.Identity?.Name, ct))
        {
            TempData["Saved"] = "Saved.";
        }
        else
        {
            TempData["Error"] = "Unknown family.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(string id, string assetCode, string? note, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var error = await AssetFamilyStore.AddMemberAsync(conn, id, assetCode, note, User.Identity?.Name, ct);
        if (error is null)
        {
            TempData["Saved"] = "Member saved. The fold applies to the next page render; stored data is unchanged.";
        }
        else
        {
            TempData["Error"] = error;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(string id, string assetCode, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await AssetFamilyStore.RemoveMemberAsync(conn, assetCode, ct);
        TempData["Saved"] = $"{assetCode} removed. It is now its own family.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var error = await AssetFamilyStore.DeleteAsync(conn, id, ct);
        if (error is null)
        {
            TempData["Saved"] = "Family deleted.";
            return RedirectToAction(nameof(Index));
        }

        TempData["Error"] = error;
        return RedirectToAction(nameof(Details), new { id });
    }
}

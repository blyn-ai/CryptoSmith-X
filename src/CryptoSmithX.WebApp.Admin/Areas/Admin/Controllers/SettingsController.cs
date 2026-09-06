using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Admin.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Areas.Admin.Controllers;

/// <summary>
/// System → Settings: the global market-data values the Hub reads live. Edited here, applied within
/// a minute (the Hub's settings cache plus one loop interval). Validated by kind before it is saved.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class SettingsController : Controller
{
    private readonly Db _db;

    public SettingsController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var settings = await SettingStore.ListAsync(conn, ct);
        return View(settings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string key, string? value, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var error = await SettingStore.UpdateAsync(conn, key, value ?? "", User.Identity?.Name, ct);
        if (error is not null)
        {
            TempData["Error"] = $"{key}: {error}";
        }
        else
        {
            TempData["Saved"] = $"{key} saved — applies within a minute.";
        }

        return RedirectToAction(nameof(Index));
    }
}

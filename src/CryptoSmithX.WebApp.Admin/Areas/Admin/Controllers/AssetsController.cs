using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Admin.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Areas.Admin.Controllers;

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

    /// <summary>
    /// One asset across every venue that lists it, at one instant — reached by clicking the
    /// normalised name on the market-state page, which carries its moment across. Kept apart from
    /// <see cref="Details"/> on purpose: that page is the registry entry an operator edits, this one
    /// is a reading of the market and owns nothing.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> At(string id, string? at, short? tf, CancellationToken ct)
    {
        // Parsed as UTC explicitly. A page whose whole point is which instant you are looking at
        // must not quietly reinterpret it in the server's local time.
        var moment = DateTime.TryParse(
            at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : DateTime.UtcNow;

        // Only the timeframes the rollup actually produces; anything else would render an empty
        // table that looks like missing data rather than a bad request.
        var timeframe = tf is { } t && Timeframes.Contains(t) ? t : (short)1;

        await using var conn = await _db.OpenAsync(ct);
        var slice = await AssetStore.AtAsync(conn, id, moment, timeframe, ct);
        return slice is null ? NotFound() : View(slice);
    }

    internal static readonly short[] Timeframes = [1, 5, 15, 60, 240, 720, 1440];

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
        string id, string? segmentCode, string alias, string multiplier, string? note, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var error = await AssetStore.AddAliasAsync(conn, id, segmentCode, alias, multiplier, note, ct);
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
    public async Task<IActionResult> DeleteAlias(string id, string? segmentCode, string alias, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await AssetStore.DeleteAliasAsync(conn, segmentCode, alias, ct);
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

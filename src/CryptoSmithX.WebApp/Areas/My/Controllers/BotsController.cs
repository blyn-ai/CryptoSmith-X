using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Auth;
using CryptoSmithX.WebApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.My.Controllers;

/// <summary>
/// A user's own bots. The tenant comes from the signed-in claim and is threaded into every query —
/// a foreign bot id resolves to nothing and returns 404, so the scoping lives in SQL, not the view.
/// </summary>
[Area("My")]
[Authorize(Roles = "user")]
public sealed class BotsController : Controller
{
    private readonly Db _db;

    public BotsController(Db db) => _db = db;

    private string Tenant => TenantScope.Require(User.FindFirst("tenantCode")?.Value);

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var bots = await BotStore.ListAsync(conn, Tenant, ct);
        return View(bots);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var bot = await BotStore.GetAsync(conn, id, Tenant, ct);
        return bot is null ? NotFound() : View(bot);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePolicy(int id, string? policyJson, CancellationToken ct)
    {
        if (!PolicyJson.TryValidate(policyJson, out var normalised))
        {
            TempData["Error"] = "The policy is not valid JSON.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await using var conn = await _db.OpenAsync(ct);
        var saved = await BotStore.SavePolicyAsync(conn, id, Tenant, normalised, ct);
        if (!saved)
        {
            return NotFound();
        }

        TempData["Saved"] = "Policy saved.";
        return RedirectToAction(nameof(Details), new { id });
    }
}

using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Admin.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class TenantsController : Controller
{
    private readonly Db _db;

    public TenantsController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var tenants = (await conn.QueryAsync<TenantRow>(new CommandDefinition(
            "select code as \"Code\", name as \"Name\", created_at as \"CreatedAt\" from tenant order by code",
            cancellationToken: ct))).ToList();
        return View(tenants);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? code, string? name, CancellationToken ct)
    {
        code = code?.Trim().ToUpperInvariant() ?? "";
        name = name?.Trim() ?? "";
        if (code.Length == 0 || name.Length == 0)
        {
            TempData["Error"] = "Code and name are both required.";
            return RedirectToAction(nameof(Index));
        }

        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "insert into tenant (code, name) values (@code, @name) on conflict (code) do nothing",
            new { code, name },
            cancellationToken: ct));

        return RedirectToAction(nameof(Index));
    }
}

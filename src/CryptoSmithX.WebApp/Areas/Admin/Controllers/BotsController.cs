using System.Text.Json;
using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Auth;
using CryptoSmithX.WebApp.Data;
using CryptoSmithX.WebApp.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class BotsController : Controller
{
    private readonly Db _db;

    public BotsController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var bots = await BotStore.ListAsync(conn, tenantCode: null, ct);
        ViewData["Tenants"] = (await conn.QueryAsync<string>(new CommandDefinition(
            "select code from tenant order by code", cancellationToken: ct))).ToList();
        return View(bots);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? tenantCode, string? botInstanceId, string? name, CancellationToken ct)
    {
        tenantCode = tenantCode?.Trim() ?? "";
        botInstanceId = botInstanceId?.Trim() ?? "";
        name = name?.Trim() ?? "";
        if (tenantCode.Length == 0 || botInstanceId.Length == 0 || name.Length == 0)
        {
            TempData["Error"] = "Tenant, instance id and name are all required.";
            return RedirectToAction(nameof(Index));
        }

        var token = BotTokens.Generate();
        await using var conn = await _db.OpenAsync(ct);

        try
        {
            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                insert into bot (tenant_code, bot_instance_id, name, token_hash, is_enabled)
                values (@tenantCode, @botInstanceId, @name, @tokenHash, true)
                returning id
                """,
                new { tenantCode, botInstanceId, name, tokenHash = BotTokens.Hash(token) },
                cancellationToken: ct));

            TempData["NewToken"] = JsonSerializer.Serialize(new NewTokenNotice(botInstanceId, token));
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505" || ex.SqlState == "23503")
        {
            // 23505 unique_violation (instance id taken), 23503 foreign_key_violation (no such tenant).
            TempData["Error"] = "That instance id is already taken, or the tenant does not exist.";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var bot = await BotStore.GetAsync(conn, id, tenantCode: null, ct);
        if (bot is null)
        {
            return NotFound();
        }

        if (TempData["NewToken"] is string json)
        {
            ViewData["NewToken"] = JsonSerializer.Deserialize<NewTokenNotice>(json);
        }

        return View(bot);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(int id, bool enabled, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update bot set is_enabled = @enabled, updated_at = now() where id = @id",
            new { id, enabled },
            cancellationToken: ct));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateToken(int id, CancellationToken ct)
    {
        var token = BotTokens.Generate();
        await using var conn = await _db.OpenAsync(ct);
        var instanceId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "update bot set token_hash = @tokenHash, updated_at = now() where id = @id returning bot_instance_id",
            new { id, tokenHash = BotTokens.Hash(token) },
            cancellationToken: ct));
        if (instanceId is null)
        {
            return NotFound();
        }

        TempData["NewToken"] = JsonSerializer.Serialize(new NewTokenNotice(instanceId, token));
        return RedirectToAction(nameof(Details), new { id });
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
        var saved = await BotStore.SavePolicyAsync(conn, id, tenantCode: null, normalised, ct);
        if (!saved)
        {
            return NotFound();
        }

        TempData["Saved"] = "Policy saved.";
        return RedirectToAction(nameof(Details), new { id });
    }
}

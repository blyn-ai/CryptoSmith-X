using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// The header search box. One query against everything addressable — instruments, assets,
/// exchanges, bots, clients. A single hit redirects straight to its page; anything else
/// renders a grouped results list. Server-side only, like the rest of the console.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class SearchController : Controller
{
    private readonly Db _db;

    public SearchController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        q = q?.Trim() ?? "";
        if (q.Length == 0)
        {
            return Redirect("/Admin");
        }

        await using var conn = await _db.OpenAsync(ct);
        var like = $"%{q}%";

        var hits = (await conn.QueryAsync<SearchHit>(new CommandDefinition(
            """
            select 'instrument' as "Kind", i.exchange_symbol as "Title",
                   i.exchange_code || ' · ' || i.base_asset || '/' || i.quote_asset as "Note",
                   '/Admin/Instruments/Details/' || i.id as "Url"
              from exchange_instrument i
             where i.exchange_symbol ilike @like or i.base_asset ilike @like or i.base_asset_raw ilike @like
            union all
            select 'asset', a.code, coalesce(a.name, ''), '/Admin/Assets/Details/' || a.code
              from asset a
             where a.code ilike @like or a.name ilike @like
            union all
            select 'exchange', e.name, e.code || ' · ' || e.status, '/Admin/Exchanges/Details/' || e.code
              from exchange e
             where e.code ilike @like or e.name ilike @like
            union all
            select 'bot', b.bot_instance_id, b.tenant_code, '/Admin/Bots/Details/' || b.id
              from bot b
             where b.bot_instance_id ilike @like or b.name ilike @like
            union all
            select 'client', t.name, t.code, '/Admin/Clients/Details/' || t.code
              from tenant t
             where t.code ilike @like or t.name ilike @like
            limit 50
            """,
            new { like },
            cancellationToken: ct))).ToList();

        if (hits.Count == 1)
        {
            return Redirect(hits[0].Url);
        }

        ViewData["Query"] = q;
        return View(hits);
    }
}

using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.ViewComponents;

/// <summary>
/// The grouped left navigation. Admin gets the full tree with live badge counts; a tenant user
/// gets a two-item nav. Badges are one cheap query — the number of exchanges not healthy, and the
/// number of bots — so the operator sees "something needs attention" from any page.
/// </summary>
public sealed class SideNavViewComponent : ViewComponent
{
    private readonly Db _db;

    public SideNavViewComponent(Db db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var isAdmin = User.IsInRole("admin");
        if (!isAdmin)
        {
            return View(new SideNavModel([
                new NavGroup("Fleet", [
                    new NavItem("My bots", "/My/Bots"),
                    new NavItem("Sharing", "#", "soon", Soon: true),
                ]),
            ]));
        }

        int problems = 0, bots = 0;
        try
        {
            await using var conn = await _db.OpenAsync(HttpContext.RequestAborted);
            // A problem exchange: enabled, and some collector is failing or its snapshot is stale.
            problems = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                select count(*) from segment e
                 where e.status = 'enabled'
                   and exists (select 1 from collector_status s
                                where s.segment_code = e.code
                                  and (s.consecutive_failures > 0
                                       or s.last_success_at < now() - interval '3 minutes'))
                """,
                cancellationToken: HttpContext.RequestAborted));
            bots = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "select count(*) from bot", cancellationToken: HttpContext.RequestAborted));
        }
        catch
        {
            // The nav must render even if a count query fails; badges just go blank.
        }

        string? p = problems > 0 ? problems.ToString() : null;
        string? b = bots > 0 ? bots.ToString() : null;

        return View(new SideNavModel([
            new NavGroup("Operations", [
                new NavItem("Dashboard", "/Admin", p),
                new NavItem("Exchanges", "/Admin/Exchanges", p),
                new NavItem("Datasets", "/Admin/Datasets"),
                new NavItem("Assets", "/Admin/Assets"),
                // Sits between assets and pairs on purpose: a family is the level above the
                // canonical asset and below the pair, and it is only ever read while looking at
                // one of those two.
                new NavItem("Families", "/Admin/Families"),
                new NavItem("Pairs", "/Admin/Pairs"),
                new NavItem("Instruments", "/Admin/Instruments"),
                // Built in the same pass as the feeds regrouping and then left unreachable: no nav
                // entry, and nothing else links to it either. A page that answers "what did the
                // market look like at 12:04:31" is worthless if you have to know its URL.
                new NavItem("Market state", "/Admin/MarketState"),
            ]),
            new NavGroup("Fleet", [
                new NavItem("Bots", "/Admin/Bots", b),
                new NavItem("Tenants", "/Admin/Tenants"),
            ]),
            new NavGroup("Clients", [
                new NavItem("Clients", "/Admin/Clients"),
                new NavItem("Sharing", "#", "soon", Soon: true),
                new NavItem("Billing", "#", "soon", Soon: true),
            ]),
            new NavGroup("System", [
                new NavItem("Users", "#", "soon", Soon: true),
                new NavItem("Audit", "#", "soon", Soon: true),
                new NavItem("Settings", "/Admin/Settings"),
            ]),
        ]));
    }
}

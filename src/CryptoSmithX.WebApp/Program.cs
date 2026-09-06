using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Api;
using CryptoSmithX.WebApp.Live;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Sentry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Sentry: errors go to the shared cryptosmith-x project, tagged by component so hub/api/webapp are
// separable in the UI. The DSN comes from the environment (Sentry__Dsn); with none set — local dev
// — the SDK stays disabled and silent. Errors only, no performance tracing.
var sentryDsn = builder.Configuration["Sentry:Dsn"];
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(o =>
    {
        o.Dsn = sentryDsn;
        o.Environment = builder.Environment.EnvironmentName;
        o.TracesSampleRate = 0;
    });
}

// TLS is terminated by an upstream proxy (traefik) that sets X-Forwarded-Proto. Honour it so
// Request.IsHttps is true behind the proxy and the auth cookie's SameAsRequest policy marks it
// Secure. Inert on http://localhost, which browsers already treat as a secure context. The proxy
// is not loopback inside the compose network, so trust it explicitly.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));

// One dedicated LISTEN connection for the whole process (not the pool above — see LiveNotifier's own
// comment on why). Registered as itself, not just IHostedService, so controllers can subscribe to it.
builder.Services.AddSingleton<LiveNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveNotifier>());

// Cookie auth only. AddCookie is the seam an OIDC provider (authentik) can be added beside later;
// nothing here is hand-rolled.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/";
        o.LogoutPath = "/auth/logout";
        o.AccessDeniedPath = "/";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews();

// Cookie keys must survive a container restart, otherwise every redeploy logs everyone out.
var keysDir = builder.Configuration["DataProtection:KeysDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("CryptoSmithX.WebApp");

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    SentrySdk.ConfigureScope(scope => scope.SetTag("component", "webapp"));
}

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

app.UseForwardedHeaders();

// A missing page answered with a blank body and no content type, which is what every
// wrong URL on the domain looked like. Re-execute into the branded page instead — but
// not under /api, where an ingest client asking for JSON must not be handed HTML.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found"));

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// The public static pages moved from Cloudflare Pages into wwwroot/ui-mocks; their
// canonical URLs are shared with people and must keep working.
app.MapGet("/zurnalas", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "ui-mocks", "zurnalas.html"), "text/html"));
// Prototypes: reachable by anyone holding the link, deliberately kept out of search.
// They render invented numbers for products that do not exist — the Arena mock alone
// shows "128 strategies · telemetry verified 100% · 4,489 observations" — and a search
// result carrying those under the company's own domain stops being a mockup and
// becomes a claim, from a company whose published position is that its figures are
// research data and not an offer. noindex rather than a robots Disallow on purpose:
// a disallowed URL can still be listed, because the crawler never gets far enough to
// read the instruction telling it not to.
//
// They live under /feature-demo/ because the product is about to want their names.
// The real Arena takes /arena; a mock sitting on the address of the thing it mocks is
// a collision waiting for the deploy that loses one of them. The prefix says what
// these are — a demonstration of a feature — so the top-level names stay free for
// features that exist.
// /feature-demo/ui-mocks is the superseded Lithuanian landing, a near-duplicate of the front page.
//
// The third element is whether the OLD top-level address still redirects here. It is false for
// exactly one entry, and that is the whole point of the flag existing rather than the entry being
// deleted: /arena now belongs to the real Arena, which is a different container behind the same
// traefik, so this application must stop answering there — but /feature-demo/arena is the mock's
// permanent address and has to keep working. Dropping the tuple would have taken both.
foreach (var (name, page, redirectFromRoot) in new[]
         {
             ("config", "config.html", true),
             ("agent", "agent.html", true),
             // The redirect that used to stand here was always temporary, and the 302 was chosen
             // for this day: a 301 would have pinned every visitor who once opened the mock to the
             // mock forever, out of their own cache, where no deploy could reach them. Nobody's
             // browser is holding a stale answer, so /arena is free to become Arena.
             ("arena", "arena.html", false),
             ("strategy-modeler", "strategy-modeler.html", true),
             ("pairs-monitor", "pairs-monitor.html", true),
             // New, so no top-level address was ever shared and none is claimed: a prototype
             // should not hold a name a real feature might want.
             ("live-bots", "live-bots.html", false),
             ("ui-mocks", "index.html", true),
         })
{
    var file = page;
    app.MapGet($"/feature-demo/{name}", (HttpContext ctx, IWebHostEnvironment env) =>
    {
        ctx.Response.Headers["X-Robots-Tag"] = "noindex";
        return Results.File(Path.Combine(env.WebRootPath, "ui-mocks", file), "text/html");
    });

    if (!redirectFromRoot)
    {
        continue;
    }

    // The old top-level address was shared with people, so it keeps answering. Found
    // and not Moved Permanently on purpose: a 301 is cached by the browser for as long
    // as it likes, and the top-level names are the ones real features come to claim — a
    // permanent redirect would send the people who once opened a mock to the mock
    // forever, from their own cache, where no deploy can reach them.
    app.MapGet($"/{name}", () => Results.Redirect($"/feature-demo/{name}", permanent: false));
}

// The re-execute target. Sends the status through itself so the page a person reads
// and the code a crawler records say the same thing.
app.MapGet("/not-found", async (HttpContext ctx, IWebHostEnvironment env) =>
{
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers["X-Robots-Tag"] = "noindex";
    await ctx.Response.SendFileAsync(Path.Combine(env.WebRootPath, "ui-mocks", "404.html"));
});

app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapIngestEndpoints();

await app.RunAsync();

// Exposed so the test project can reference the composition assembly.
public partial class Program;

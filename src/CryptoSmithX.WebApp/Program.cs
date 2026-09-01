using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

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

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// The public static pages moved from Cloudflare Pages into wwwroot/ui-mocks; their
// canonical URLs are shared with people and must keep working.
app.MapGet("/zurnalas", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "ui-mocks", "zurnalas.html"), "text/html"));
app.MapGet("/config", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "ui-mocks", "config.html"), "text/html"));
app.MapGet("/ui-mocks", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "ui-mocks", "index.html"), "text/html"));

app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapIngestEndpoints();

await app.RunAsync();

// Exposed so the test project can reference the composition assembly.
public partial class Program;

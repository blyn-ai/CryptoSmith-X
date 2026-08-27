using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Api;
using CryptoSmithX.WebApp.Auth;
using CryptoSmithX.WebApp.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

// `dotnet run -- hash-password <pwd>` prints a PBKDF2 hash and exits, so the operator can fill
// WebApp:Users without a running app. This is the only non-web entry point.
if (args is ["hash-password", var pwd])
{
    Console.WriteLine(PasswordHasher.Hash(pwd));
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.Configure<WebAppOptions>(builder.Configuration.GetSection(WebAppOptions.SectionName));
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

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapIngestEndpoints();

await app.RunAsync();

// Exposed so the test project can reference the composition assembly.
public partial class Program;

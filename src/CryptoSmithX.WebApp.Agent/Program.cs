using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Agent;
using CryptoSmithX.WebApp.Agent.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Sentry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Sentry: the shared cryptosmith-x project, tagged by component so hub/api/webapp-admin/studio/agent
// are separable in the UI. The DSN comes from the environment (Sentry__Dsn); with none set — local
// dev — the SDK stays disabled and silent. Errors only, no performance tracing.
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

// TLS is terminated by traefik, which sets X-Forwarded-Proto. Honour it so Request.IsHttps is true
// behind the proxy and the auth cookie's SameAsRequest policy marks itself Secure — on this surface
// that cookie is the whole session, so getting it wrong sends it in clear. Inert on http://localhost,
// which browsers already treat as a secure context. The proxy is not loopback inside the compose
// network, so trust it explicitly.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Our database. The only thing read from it today is webapp_user, which is why the role is
// marketdata and not studio_reader: the reader role created by 0025 is granted the market
// showcase and nothing else, so a sign-in against it would fail on the one table this needs.
builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));

// The trading bot's OWN database — a second PostgreSQL, not ours, holding the runtime overrides
// both futures workers re-read about every ten seconds. ONE bot serves both contours, which is the
// owner's instruction and not an oversight: there is one bot, and a test copy of a page about it
// that wrote to a different database would be editing a profile nobody trades on.
//
// Its own type rather than a second Db, because a container cannot hold two singletons of one type
// and because writing our market data into the bot's schema should be a compile error.
builder.Services.AddSingleton(_ => new BotDb(
    builder.Configuration.GetConnectionString("TradingBotDatabase")
    ?? throw new InvalidOperationException("ConnectionStrings:TradingBotDatabase is not configured.")));

// The bot's HTTP API. Read by the health probe only: it is the cheapest question whose answer is
// "this container can see the bot at all", and it is a different path from the database above —
// the two fail separately and should be reported separately.
builder.Services.AddHttpClient("trading-bot", (sp, http) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["TradingBot:BaseUrl"]
        ?? throw new InvalidOperationException("TradingBot:BaseUrl is not configured.");
    http.BaseAddress = new Uri(baseUrl);
    http.Timeout = TimeSpan.FromSeconds(10);
});

// Cookie auth, the same shape as the admin console's, because it is the same account table.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        // ITS OWN NAME, and this is not cosmetic — it is the difference between the console
        // working and an endless bounce with no error on it.
        //
        // The admin console serves the same host and issues the framework's default
        // `.AspNetCore.Cookies` at path "/". This application's cookie sits at "/agent", because
        // that is its PathBase. Both paths match a request to /agent/anything, so the browser
        // sends BOTH, and the handler reads Request.Cookies[name] — the FIRST of the two. That is
        // the console's, encrypted under a different application name and a different key ring, so
        // it fails to decrypt, the request is anonymous, and the visitor is challenged back to a
        // form they just filled in correctly. No wrong-password message, because the password was
        // right. Only people who had signed into /admin in the same browser could see it.
        //
        // Renaming the console's cookie instead would sign out everyone already holding one, and
        // this application is the newcomer, so it takes the new name.
        o.Cookie.Name = ".CryptoSmithX.Agent";

        // Relative to PathBase, which the framework prepends when it redirects — so this is
        // "/agent" as seen from outside. The sign-in form IS this application's home page, so an
        // unauthenticated request for the parameters page comes back to it rather than to some
        // separate /login address that would then be a second name for the same screen.
        o.LoginPath = "/";
        o.AccessDeniedPath = "/";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews();

// Cookie keys must survive a container restart, otherwise every redeploy signs everyone out. The
// admin console mounts a volume for exactly this; so does this one.
var keysDir = builder.Configuration["DataProtection:KeysDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    // Its own application name, deliberately: sharing one with the admin console would make a
    // cookie issued by either app decrypt in the other, and these are different surfaces.
    .SetApplicationName("CryptoSmithX.WebApp.Agent");

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    SentrySdk.ConfigureScope(scope => scope.SetTag("component", "agent"));
}

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

// The agent lives under /agent, and the prefix is stripped HERE — in the app — and nowhere else.
// Traefik routes by path prefix with no stripPrefix middleware, for the reason written out at
// length beside Studio's copy of this line: two places removing the same prefix produce correct
// URLs only while both agree, and the day someone reorders proxy middleware every link breaks at
// once, silently, in production only.
//
// Before everything else: routing, static files and link generation all read PathBase.
//
// Unlike Studio there is no root rewrite, and there should not be. Studio needs one because
// traefik hands it a bare "/" — it owns the site's front door. This application is only ever
// addressed as /agent/…, so a request arriving here with no prefix is not a visitor, it is a
// misconfiguration, and it should look like one.
app.UsePathBase("/agent");

app.UseForwardedHeaders();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Whether this container can actually reach the two things it was given: our database and the bot.
// Anonymous on purpose — it is the first thing to ask when the page misbehaves, and needing to sign
// in to find out why sign-in is broken is a circle. It states reachability and nothing else: no
// figures, no account, no connection string, nothing a stranger learns from beyond up or down.
app.MapGet("/health", async (Db db, BotDb bot, IHttpClientFactory http, CancellationToken ct) =>
{
    async Task<string> ReachableAsync(Func<CancellationToken, ValueTask<Npgsql.NpgsqlConnection>> open)
    {
        try
        {
            await using var conn = await open(ct);
            return "ok";
        }
        catch (Exception e)
        {
            return "unreachable: " + e.GetType().Name;
        }
    }

    var database = await ReachableAsync(db.OpenAsync);
    // The bot's database is the one this application WRITES to, so it is reported on its own line.
    // It is a different machine from ours on one of the two contours, and the day the route to it
    // is gone the parameters page is the only thing that stops working — this says so in one call
    // instead of leaving it to be discovered by an owner trying to change a margin.
    var botDatabase = await ReachableAsync(bot.OpenAsync);

    var botApi = "ok";
    try
    {
        using var response = await http.CreateClient("trading-bot").GetAsync("/api/bot-status", ct);
        botApi = response.IsSuccessStatusCode ? "ok" : "http " + (int)response.StatusCode;
    }
    catch (Exception e)
    {
        botApi = "unreachable: " + e.GetType().Name;
    }

    var healthy = database == "ok" && botDatabase == "ok" && botApi == "ok";
    return Results.Json(
        new { database, tradingBotDatabase = botDatabase, tradingBotApi = botApi },
        statusCode: healthy ? 200 : 503);
});

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

await app.RunAsync();

// Exposed so a test project can reference the composition assembly.
public partial class Program;

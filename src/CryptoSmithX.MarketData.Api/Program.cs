using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Api;
using Scalar.AspNetCore;
using Sentry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Sentry: shared cryptosmith-x project, tagged component=api. DSN from the environment
// (Sentry__Dsn); empty locally means the SDK is disabled. Errors only, no tracing.
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

builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));

// The OpenAPI document for the read-only /v1 surface, and a Scalar reference UI over it.
builder.Services.AddOpenApi();

// These payloads are long runs of similar JSON — a page of book frames or a 48 h tape compresses by
// an order of magnitude, and the bot reading them is on the other side of the internet. Same
// registration Studio already uses; brotli first, gzip for callers that do not offer it.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = [.. Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes, "application/json"];
});

// ── Browsers may read this surface from the public site ──────────────────────────────────────
//
// FOUND BY LOOKING AT THE SITE, not at this project. blynai.eu renders its venue coverage from
// /api/v1/exchanges and /api/v1/coverage, and every one of those calls had been failing since the
// day it was written: the site is a DIFFERENT ORIGIN (blynai.eu against
// cryptosmithx.blynai.eu), no Access-Control-Allow-Origin came back, and the browser refused the
// read. curl saw 200 the whole time, which is exactly why it went unnoticed.
//
// What the page did instead is the honest part of the failure and is why nobody noticed sooner: it
// keeps the last known text in its own markup and leaves it standing when the call fails. So the
// site kept saying "4 live · 13 planned · 370 instruments" — true when that HTML was written and
// wrong for every venue enabled since, Avantis included.
//
// Named origins rather than "*": this surface is public and read-only, but a wildcard is a
// statement about every site on the internet, and the two that actually render it can be named.
// Localhost is here so the site can be developed against the live API without a proxy.
const string SiteOrigins = "site";
builder.Services.AddCors(o => o.AddPolicy(SiteOrigins, p => p
    .WithOrigins(
        "https://blynai.eu",
        "https://www.blynai.eu",
        "http://localhost:8080",
        "http://127.0.0.1:8080")
    .WithMethods("GET")
    // No credentials and no custom request headers: a public read needs neither, and asking for
    // them would widen what this policy permits for nothing.
    .WithHeaders("Accept", "Content-Type")));

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    SentrySdk.ConfigureScope(scope => scope.SetTag("component", "api"));
}

// /api/v1/... is the same surface as /v1/..., under the name a caller reaches for when they think
// of us as "the API" rather than as "version 1 of something". Traefik already routes /api here —
// its rule has matched PathPrefix(`/api`) all along for the sake of the /api -> /scalar/v1 front
// door — so the path arrived intact and the app answered 404 to a request that had reached it.
//
// A rewrite, NOT a second MapGroup("/api/v1") beside the first. Mapping the seven endpoints twice
// would publish fourteen operations in /openapi/v1.json and list every endpoint twice in the Scalar
// page, which damages the one thing that document exists to be — an exact statement of the surface.
// It would also leave two route tables to keep in step, and the next endpoint would land in one.
//
// StartsWithSegments matches whole segments, so /apiv1/... and /api/v2/... are left alone; query
// strings live outside Request.Path and are untouched.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1", out var rest))
    {
        context.Request.Path = "/v1" + rest;
    }

    await next(context);
});

// Load-bearing, and the reason the first attempt at this silently did nothing: with no explicit
// call, WebApplication inserts routing at the START of the pipeline — ahead of the middleware
// above — so the endpoint is chosen from the un-rewritten path and /api/v1/anything 404s no matter
// what the rewrite does afterwards. Verified both ways against a running server before landing.
app.UseRouting();

// After UseRouting and before the endpoints, which is where the CORS middleware is required to sit
// for a preflight to be answered rather than routed.
app.UseCors(SiteOrigins);

// /openapi/v1.json (the document) and /scalar/v1 (the interactive page).
app.UseResponseCompression();

app.MapOpenApi();
app.MapScalarApiReference();

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
// Compose ordering makes this a formality, the check makes a mis-start loud instead of weird.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

// No per-endpoint RequireCors: these two return void, and UseCors above already applies the named
// policy to everything that passes through the pipeline. Chaining it per endpoint would be the
// same policy said twice, in a place the compiler cannot even accept it.
app.MapMarketDataApi();
app.MapHistoryApi();

await app.RunAsync();

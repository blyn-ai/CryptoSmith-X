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

// /openapi/v1.json (the document) and /scalar/v1 (the interactive page).
app.MapOpenApi();
app.MapScalarApiReference();

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
// Compose ordering makes this a formality, the check makes a mis-start loud instead of weird.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

app.MapMarketDataApi();

await app.RunAsync();

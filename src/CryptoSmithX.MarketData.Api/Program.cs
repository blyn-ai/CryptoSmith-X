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

// /openapi/v1.json (the document) and /scalar/v1 (the interactive page).
app.MapOpenApi();
app.MapScalarApiReference();

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
// Compose ordering makes this a formality, the check makes a mis-start loud instead of weird.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

app.MapMarketDataApi();

await app.RunAsync();

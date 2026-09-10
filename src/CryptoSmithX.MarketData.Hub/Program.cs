using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Hub;
using CryptoSmithX.MarketData.Hub.Ingestion;
using CryptoSmithX.MarketData.Hub.Live;
using Sentry;
using Sentry.Extensions.Logging;

// A web host, and only just: the Hub's work is still the collectors, and the one endpoint it serves
// is /live — the venues' own sockets relayed to the studio at a tick no database round trip could
// keep up with. Kestrel listens on the compose network and nowhere else (no ports:, no Traefik
// route), so "no authentication" here means "the network is the perimeter", not "open".
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Sentry: shared cryptosmith-x project, tagged component=hub. Still hooked to the LOGGING pipeline
// rather than the web host — the errors worth an event here come from collector loops, which are
// hosted services and outside any request, exactly as they were before this process served HTTP.
// DSN from the environment (Sentry__Dsn); empty locally means the SDK is disabled.
var sentryDsn = builder.Configuration["Sentry:Dsn"];
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.Logging.AddSentry(o =>
    {
        o.Dsn = sentryDsn;
        o.Environment = builder.Environment.EnvironmentName;
        o.TracesSampleRate = 0;
    });
}

builder.WebHost.UseUrls("http://0.0.0.0:8080");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));
// All market-data configuration lives in the database; the Hub reads it live, no IOptions.
builder.Services.AddSingleton<DbSettings>();

// The live path. The registry is the seam between the two halves of this process: the worker fills
// it as exchanges come and go, the endpoint reads it. Registered as the concrete type AND the
// interface so the worker can write and the endpoint can only read.
builder.Services.AddSingleton<AdapterRegistry>();
builder.Services.AddSingleton<IAdapterRegistry>(sp => sp.GetRequiredService<AdapterRegistry>());
builder.Services.AddSingleton<InstrumentMap>();

builder.Services.AddHostedService<ExchangeWorker>();

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    SentrySdk.ConfigureScope(scope => scope.SetTag("component", "hub"));
}

app.MapLive();

// The schema is owned by CryptoSmithX.Database; refuse to start on one that is missing or behind.
// Done here (not inside the worker) so a failure exits the process non-zero — inside ExecuteAsync
// it would stop the host but still return 0, and compose would restart it in a silent loop.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

await app.RunAsync();

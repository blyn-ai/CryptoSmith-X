using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Hub;
using CryptoSmithX.MarketData.Hub.Ingestion;
using Sentry;
using Sentry.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Sentry: shared cryptosmith-x project, tagged component=hub. No web host here, so it hooks the
// logging pipeline — ILogger errors and unhandled exceptions become events, warnings breadcrumbs.
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));
// All market-data configuration lives in the database; the Hub reads it live, no IOptions.
builder.Services.AddSingleton<DbSettings>();
builder.Services.AddHostedService<ExchangeWorker>();

var host = builder.Build();

if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    SentrySdk.ConfigureScope(scope => scope.SetTag("component", "hub"));
}

// The schema is owned by CryptoSmithX.Database; refuse to start on one that is missing or behind.
// Done here (not inside the worker) so a failure exits the process non-zero — inside ExecuteAsync
// it would stop the host but still return 0, and compose would restart it in a silent loop.
await Migrator.VerifyAsync(host.Services.GetRequiredService<Db>(), CancellationToken.None);

await host.RunAsync();

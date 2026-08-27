using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Hub.Ingestion;
using CryptoSmithX.MarketData.Hub.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.Configure<MarketDataOptions>(builder.Configuration.GetSection(MarketDataOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));
builder.Services.AddHostedService<ExchangeWorker>();

var host = builder.Build();

// The schema is owned by CryptoSmithX.Database; refuse to start on one that is missing or behind.
// Done here (not inside the worker) so a failure exits the process non-zero — inside ExecuteAsync
// it would stop the host but still return 0, and compose would restart it in a silent loop.
await Migrator.VerifyAsync(host.Services.GetRequiredService<Db>(), CancellationToken.None);

await host.RunAsync();

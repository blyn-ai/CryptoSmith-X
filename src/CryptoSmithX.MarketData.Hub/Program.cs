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
await host.RunAsync();

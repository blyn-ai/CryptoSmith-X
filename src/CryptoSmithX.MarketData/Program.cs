using CryptoSmithX.MarketData.Api;
using CryptoSmithX.MarketData.Ingestion;
using CryptoSmithX.MarketData.Options;
using CryptoSmithX.MarketData.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.Configure<MarketDataOptions>(builder.Configuration.GetSection(MarketDataOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("MarketData")
    ?? throw new InvalidOperationException("ConnectionStrings:MarketData is not configured.")));
builder.Services.AddHostedService<ExchangeWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapMarketDataApi();

await app.RunAsync();

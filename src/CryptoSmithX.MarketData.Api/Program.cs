using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.AddSingleton(_ => new Db(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.")));

var app = builder.Build();

// The schema is owned by CryptoSmithX.Database; refuse to serve on one that is missing or behind.
// Compose ordering makes this a formality, the check makes a mis-start loud instead of weird.
await Migrator.VerifyAsync(app.Services.GetRequiredService<Db>(), CancellationToken.None);

app.MapMarketDataApi();

await app.RunAsync();

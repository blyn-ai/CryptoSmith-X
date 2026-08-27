using CryptoSmithX.Database;

// The one-shot migrator. Applies the embedded migrations under an advisory lock and exits: 0 on
// success, non-zero on failure. Compose runs this to completion before the Hub or the Api start,
// passing the connection string in the environment as ConnectionStrings__Database. This project
// ships no appsettings.json of its own — one would collide with the Hub's and the Api's when they
// reference it — so a local run without that variable falls back to the standard localhost string.
var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var connectionString = config.GetConnectionString("Database")
    ?? "Host=localhost;Port=5432;Database=marketdata;Username=marketdata;Password=marketdata";

using var loggerFactory = LoggerFactory.Create(b => b.AddJsonConsole());
var logger = loggerFactory.CreateLogger("Migrator");

await using var db = new Db(connectionString);
try
{
    await Migrator.RunAsync(db, logger, CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Migration failed");
    return 1;
}

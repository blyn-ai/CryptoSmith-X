using CryptoSmithX.Database;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// The trading bot's OWN database — a second, entirely separate PostgreSQL from ours. It holds
/// what the bot runs on; ours holds the market data and the sign-in accounts. Nothing joins the
/// two, and nothing should: they are different systems that happen to be read by the same page.
///
/// A distinct type rather than a second <see cref="Db"/> because a container cannot hold two
/// singletons of one type, and because the day someone writes market data into the bot's schema —
/// or a bot override into ours — should be a compile error rather than an afternoon.
/// </summary>
public sealed class BotDb : IAsyncDisposable
{
    private readonly Db _db;

    public BotDb(string connectionString) => _db = new Db(connectionString);

    public ValueTask<Npgsql.NpgsqlConnection> OpenAsync(CancellationToken ct) => _db.OpenAsync(ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}

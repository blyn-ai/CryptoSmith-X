using Npgsql;

namespace CryptoSmithX.Database;

/// <summary>
/// The one place that owns the connection pool. Everything else asks it for a connection and
/// writes SQL; there is no repository layer and no mapper beyond Dapper's.
/// </summary>
public sealed class Db : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    public Db(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _source = builder.Build();
    }

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct) => _source.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}

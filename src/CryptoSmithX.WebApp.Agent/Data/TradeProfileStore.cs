using System.Data.Common;
using Dapper;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// Reads and writes <c>public.bot_config_overrides</c> in the trading bot's own database — the
/// runtime overrides both futures workers re-read about every ten seconds. Nothing here restarts
/// anything and nothing here deploys: a commit is the whole delivery mechanism, and it reaches
/// only NEW entries. Positions already open are not touched by it.
/// </summary>
public static class TradeProfileStore
{
    /// <summary>
    /// The override rows for one bot instance. Returns only what is actually stored — a missing key
    /// is a missing key, not a zero, and the caller fills it from the deployed baseline.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, decimal>> LoadAsync(
        DbConnection conn, string botInstanceId, CancellationToken ct)
    {
        // Read as two columns rather than into a record: Dapper fills a positional record by
        // constructor ORDER and by exact TYPE, and this table's value column is `numeric`. A record
        // is a trap here for no benefit — the shape wanted is a dictionary either way.
        var rows = await conn.QueryAsync<(string Key, decimal Value)>(new CommandDefinition(
            """
            select override_key, numeric_value
              from bot_config_overrides
             where bot_instance_id = @botInstanceId
            """,
            new { botInstanceId },
            cancellationToken: ct));

        // Keys outside the four are dropped rather than surfaced. Another writer may legitimately
        // own other knobs in this table; they are not this page's to show and certainly not its to
        // rewrite.
        return rows
            .Where(r => TradeProfileKeys.All.Contains(r.Key, StringComparer.Ordinal))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    /// <summary>When the instance's overrides were last written, or null if it has none.</summary>
    public static async Task<DateTimeOffset?> LastWrittenAsync(
        DbConnection conn, string botInstanceId, CancellationToken ct)
    {
        // DateTime, then converted — NOT DateTimeOffset asked of Dapper directly. Npgsql hands a
        // timestamptz back as a DateTime with Kind=Utc, and Dapper's scalar path casts rather than
        // converts, so asking for the offset type throws InvalidCastException at the first row that
        // exists. It is invisible until then: with no override rows this method is never called.
        var utc = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            select max(updated_at)
              from bot_config_overrides
             where bot_instance_id = @botInstanceId
               and override_key = any(@keys)
            """,
            new { botInstanceId, keys = TradeProfileKeys.All },
            cancellationToken: ct));

        return utc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc));
    }

    /// <summary>
    /// Writes all four keys for one instance in ONE transaction, and that is the point of the
    /// method existing at all: the workers poll this table on their own clock, so four separate
    /// statements would let one of them read a profile made of two old numbers and two new ones —
    /// a margin from before and a leverage from after, a combination the owner never chose and
    /// would never see on this screen.
    /// </summary>
    public static async Task SaveAsync(
        DbConnection conn, string botInstanceId, TradeProfile profile, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        await SaveInTransactionAsync(conn, tx, botInstanceId, profile, ct);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Writes the four worker-owned numeric overrides inside a caller's wider
    /// transaction. Strategy revisions call this so the worker cannot see a new
    /// strategy paired with old capital limits, or the reverse.
    /// </summary>
    public static Task SaveInTransactionAsync(
        DbConnection conn,
        DbTransaction tx,
        string botInstanceId,
        TradeProfile profile,
        CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            insert into bot_config_overrides
              (bot_instance_id, override_key, numeric_value, updated_at)
            values
              (@botInstanceId, 'position_margin_usd',          @positionMarginUsd,        now()),
              (@botInstanceId, 'leverage',                     @leverage,                 now()),
              (@botInstanceId, 'max_open_positions',           @maxOpenPositions,         now()),
              (@botInstanceId, 'max_open_positions_per_group', @maxOpenPositionsPerGroup, now())
            on conflict (bot_instance_id, override_key)
            do update set
              numeric_value = excluded.numeric_value,
              updated_at = now()
            """,
            new
            {
                botInstanceId,
                positionMarginUsd = profile.PositionMarginUsd,
                leverage = profile.Leverage,
                maxOpenPositions = (decimal)profile.MaxOpenPositions,
                maxOpenPositionsPerGroup = (decimal)profile.MaxOpenPositionsPerGroup,
            },
            tx,
            cancellationToken: ct));
}

using System.Data.Common;
using CryptoSmithX.WebApp.Admin.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Admin.Data;

/// <summary>
/// Clients, derived from tenant + bot + bot_event. There is no client table yet — online status is
/// computed from the newest heartbeat across a client's bots, never a stored flag. The consent
/// surface the screen is built around needs its own tables (client_consent, client_consent_log);
/// until then those parts of the view are marked as sample and migration-requested, not faked into
/// existence here.
/// </summary>
public static class ClientStore
{
    private const int OnlineSeconds = 180;

    public static async Task<IReadOnlyList<ClientListItem>> ListAsync(DbConnection conn, CancellationToken ct)
    {
        return (await conn.QueryAsync<ClientListItem>(new CommandDefinition(
            """
            select t.code as "Code", t.name as "Name",
                   (select count(*)::int from bot b where b.tenant_code = t.code) as "BotCount",
                   coalesce((select extract(epoch from now() - max(b.last_heartbeat_at))::double precision
                               from bot b where b.tenant_code = t.code) < 180, false) as "Online",
                   (select extract(epoch from now() - max(b.last_heartbeat_at))::double precision
                      from bot b where b.tenant_code = t.code) as "HeartbeatAgeSeconds",
                   (select count(*)::int from bot_event e join bot b on b.id = e.bot_id
                     where b.tenant_code = t.code and e.received_at > now() - interval '24 hours') as "Events24h"
              from tenant t order by t.code
            """,
            cancellationToken: ct))).ToList();
    }

    public static async Task<ClientDetails?> GetAsync(DbConnection conn, string code, CancellationToken ct)
    {
        var name = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select name from tenant where code = @code", new { code }, cancellationToken: ct));
        if (name is null)
        {
            return null;
        }

        var bots = (await conn.QueryAsync<(int Id, string BotInstanceId, double? Age)>(new CommandDefinition(
            """
            select id, bot_instance_id, extract(epoch from now() - last_heartbeat_at)::double precision
              from bot where tenant_code = @code order by bot_instance_id
            """,
            new { code }, cancellationToken: ct)))
            .Select(b => new ClientBot(b.Id, b.BotInstanceId, b.Age is not null and < OnlineSeconds, b.Age)).ToList();

        var events24 = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            select count(*)::int from bot_event e join bot b on b.id = e.bot_id
             where b.tenant_code = @code and e.received_at > now() - interval '24 hours'
            """,
            new { code }, cancellationToken: ct));

        var newest = bots.Where(b => b.HeartbeatAgeSeconds is not null).Select(b => b.HeartbeatAgeSeconds).DefaultIfEmpty().Min();

        return new ClientDetails(
            code, name,
            Online: newest is not null and < OnlineSeconds,
            HeartbeatAgeSeconds: newest,
            BotsOnline: bots.Count(b => b.Online),
            BotsTotal: bots.Count,
            Events24h: events24,
            Bots: bots);
    }
}

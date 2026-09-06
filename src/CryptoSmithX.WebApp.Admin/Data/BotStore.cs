using System.Data.Common;
using CryptoSmithX.WebApp.Admin.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Admin.Data;

/// <summary>
/// The bot/policy SQL shared by the admin and the user areas. A thin set of functions over a
/// connection — not a repository. The <c>tenantCode</c> argument is the whole tenant-scoping story:
/// pass a code and the query is constrained to it; pass null (admin) and it spans every tenant.
/// </summary>
public static class BotStore
{
    public static async Task<IReadOnlyList<BotListItem>> ListAsync(DbConnection conn, string? tenantCode, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<BotListItem>(new CommandDefinition(
            """
            select b.id                                                          as "Id",
                   b.tenant_code                                                 as "TenantCode",
                   b.bot_instance_id                                             as "BotInstanceId",
                   b.name                                                        as "Name",
                   b.is_enabled                                                  as "IsEnabled",
                   b.last_heartbeat_at                                           as "LastHeartbeatAt",
                   extract(epoch from now() - b.last_heartbeat_at)::double precision as "HeartbeatAgeSeconds",
                   (b.token_hash is not null)                                    as "HasToken"
              from bot b
             where (@tenantCode is null or b.tenant_code = @tenantCode)
             order by b.tenant_code, b.bot_instance_id
            """,
            new { tenantCode },
            cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>A single bot with its policy and its 100 newest events, or null if it is out of scope.</summary>
    public static async Task<BotDetails?> GetAsync(DbConnection conn, int id, string? tenantCode, CancellationToken ct)
    {
        var bot = await conn.QuerySingleOrDefaultAsync<BotHeader>(new CommandDefinition(
            """
            select b.id                  as "Id",
                   b.tenant_code         as "TenantCode",
                   b.bot_instance_id     as "BotInstanceId",
                   b.name                as "Name",
                   b.is_enabled          as "IsEnabled",
                   b.last_heartbeat_at   as "LastHeartbeatAt",
                   b.last_heartbeat_json::text as "LastHeartbeatJson",
                   coalesce(p.version, 0)      as "PolicyVersion",
                   p.policy_json::text         as "PolicyJson"
              from bot b
              left join bot_policy p on p.bot_id = b.id
             where b.id = @id
               and (@tenantCode is null or b.tenant_code = @tenantCode)
            """,
            new { id, tenantCode },
            cancellationToken: ct));

        if (bot is null)
        {
            return null;
        }

        var events = (await conn.QueryAsync<BotEventRow>(new CommandDefinition(
            """
            select event_id     as "EventId",
                   utc          as "Utc",
                   type         as "Type",
                   payload::text as "Payload",
                   received_at  as "ReceivedAt"
              from bot_event
             where bot_id = @id
             order by received_at desc
             limit 100
            """,
            new { id },
            cancellationToken: ct))).ToList();

        return new BotDetails(
            bot.Id, bot.TenantCode, bot.BotInstanceId, bot.Name, bot.IsEnabled,
            bot.LastHeartbeatAt, bot.LastHeartbeatJson, bot.PolicyVersion, bot.PolicyJson, events);
    }

    /// <summary>
    /// Upserts the policy and bumps its version, but only if the bot is in scope. Returns false when
    /// the bot does not exist or belongs to another tenant, so the caller can answer 404.
    /// </summary>
    public static async Task<bool> SavePolicyAsync(DbConnection conn, int id, string? tenantCode, string policyJson, CancellationToken ct)
    {
        var inScope = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from bot where id = @id and (@tenantCode is null or tenant_code = @tenantCode))",
            new { id, tenantCode },
            cancellationToken: ct));
        if (!inScope)
        {
            return false;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into bot_policy (bot_id, policy_json, version)
            values (@id, @policyJson::jsonb, 1)
            on conflict (bot_id) do update set
                policy_json = excluded.policy_json,
                version     = bot_policy.version + 1,
                updated_at  = now()
            """,
            new { id, policyJson },
            cancellationToken: ct));
        return true;
    }

    private sealed record BotHeader(
        int Id,
        string TenantCode,
        string BotInstanceId,
        string Name,
        bool IsEnabled,
        DateTime? LastHeartbeatAt,
        string? LastHeartbeatJson,
        int PolicyVersion,
        string? PolicyJson);
}

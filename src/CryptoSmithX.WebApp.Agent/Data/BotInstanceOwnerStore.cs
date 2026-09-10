using System.Data.Common;
using Dapper;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// Resolves the bot instance an authenticated web application account is allowed
/// to control. The mapping lives beside the bot configuration so the Agent never
/// needs a second, potentially stale owner map in its own appsettings.
/// </summary>
public static class BotInstanceOwnerStore
{
    public const string FindActiveSql =
        """
        select bot_instance_id as "BotInstanceId",
               webapp_username as "WebappUsername",
               bot_instance_public_alias as "PublicAlias"
          from bot_instance_webapp_users
         where webapp_username = @username
           and is_active = true
         order by bot_instance_id
         limit 1
        """;

    public static Task<BotInstanceOwner?> FindActiveAsync(
        DbConnection connection,
        string username,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<BotInstanceOwner>(new CommandDefinition(
            FindActiveSql,
            new { username },
            cancellationToken: cancellationToken));
}

public sealed record BotInstanceOwner(
    string BotInstanceId,
    string WebappUsername,
    string PublicAlias);

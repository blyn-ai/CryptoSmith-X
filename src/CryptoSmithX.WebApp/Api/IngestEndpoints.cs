using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Auth;
using Dapper;

namespace CryptoSmithX.WebApp.Api;

/// <summary>
/// The bot-facing webhook. Two POSTs, both authenticated by an opaque bearer token hashed and looked
/// up against <c>bot.token_hash</c> — a manual check, since there is exactly one kind of consumer.
/// Heartbeat is the ONLY channel by which policy reaches a bot: nobody calls the bot.
/// </summary>
public static class IngestEndpoints
{
    public static void MapIngestEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/ingest");

        api.MapPost("/heartbeat", Heartbeat);
        api.MapPost("/events", Events);
    }

    private static async Task<IResult> Heartbeat(HttpContext ctx, Db db, JsonElement body, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var botId = await AuthenticateAsync(ctx, conn, ct);
        if (botId is null)
        {
            return Results.Unauthorized();
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            update bot
               set last_heartbeat_at = now(),
                   last_heartbeat_json = @Json::jsonb,
                   updated_at = now()
             where id = @BotId
            """,
            new { BotId = botId.Value, Json = body.GetRawText() },
            cancellationToken: ct));

        var policy = await conn.QuerySingleOrDefaultAsync<PolicyRow>(new CommandDefinition(
            "select policy_json::text as \"PolicyJson\", version as \"Version\" from bot_policy where bot_id = @BotId",
            new { BotId = botId.Value },
            cancellationToken: ct));

        if (policy is null)
        {
            return Results.Ok(new { policyVersion = 0, policy = (JsonNode?)null });
        }

        return Results.Ok(new { policyVersion = policy.Version, policy = JsonNode.Parse(policy.PolicyJson) });
    }

    private static async Task<IResult> Events(HttpContext ctx, Db db, IngestEvent[] events, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var botId = await AuthenticateAsync(ctx, conn, ct);
        if (botId is null)
        {
            return Results.Unauthorized();
        }

        var accepted = 0;
        foreach (var e in events)
        {
            // on conflict do nothing makes a re-POSTed batch harmless — the contract with the outbox.
            accepted += await conn.ExecuteAsync(new CommandDefinition(
                """
                insert into bot_event (bot_id, event_id, utc, type, payload)
                values (@BotId, @EventId, @Utc, @Type, @Payload::jsonb)
                on conflict (bot_id, event_id) do nothing
                """,
                new
                {
                    BotId = botId.Value,
                    e.EventId,
                    // Bots stamp events in local time (Europe/Vilnius = +02:00/+03:00). Npgsql only
                    // writes offset-0 DateTimeOffset to timestamptz, so normalise here — otherwise a
                    // single non-UTC event 500s and jams the outbox on a batch it can never ack.
                    Utc = e.Utc.ToUniversalTime(),
                    e.Type,
                    Payload = e.Payload.GetRawText(),
                },
                cancellationToken: ct));
        }

        return Results.Ok(new { accepted, skipped = events.Length - accepted });
    }

    /// <summary>Returns the bot id for a valid, enabled token, or null. No handler for one consumer.</summary>
    private static async Task<int?> AuthenticateAsync(HttpContext ctx, System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        // The scheme is case-insensitive per RFC 7235; "bearer <t>" is as valid as "Bearer <t>".
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return null;
        }

        var hash = BotTokens.Hash(token);
        return await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "select id from bot where token_hash = @hash and is_enabled = true",
            new { hash },
            cancellationToken: ct));
    }

    public sealed record IngestEvent(string EventId, DateTimeOffset Utc, string Type, JsonElement Payload);

    private sealed record PolicyRow(string PolicyJson, int Version);
}

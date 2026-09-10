using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoSmithX.MarketData.Connectors.Market;
using Microsoft.AspNetCore.Http.Features;

namespace CryptoSmithX.MarketData.Hub.Live;

/// <summary>
/// <c>GET /live?instruments=1,2,3</c> — what the venues' own sockets are holding, as Server-Sent
/// Events, on a fixed 200 ms tick.
///
/// <b>Internal only.</b> There is no route for this in <c>deploy/traefik/cryptosmithx.yml</c> and
/// the hub publishes no ports in compose, so the only caller that can reach it is another container
/// on the default network — the studio, at <c>http://hub:8080/live</c>. That is why there is no
/// authentication here: the perimeter is the network, and adding a token would be a secret to
/// rotate guarding an address nothing outside can dial.
///
/// <b>Why a poll loop and not a subscription.</b> The feeds keep caches, not events (see
/// <see cref="Connectors.Streaming.MarketCache{T}"/>), so this reads them on its own tick.
/// Conflation is then structural rather than coded: whatever a venue sent between two ticks, one
/// value per symbol is what is there to read. The write happens inside the same loop, in sequence —
/// so a write that overruns its tick delays the next read instead of queueing behind it, and a slow
/// reader skips a frame by construction rather than by a policy someone has to maintain.
///
/// The tick is 200 ms and is not configurable. It is a budget, not a preference: everything above
/// it — one connection per studio process, one room per asset, one computation per tick — was sized
/// against it.
/// </summary>
public static class LiveEgress
{
    /// <summary>The live tick. A constant on purpose — see the class remarks.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    /// <summary>Under the 30 s idle timeout proxies commonly take, same as the studio's own stream.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapLive(this IEndpointRouteBuilder routes) =>
        routes.MapGet("/live", StreamAsync);

    private static async Task StreamAsync(
        HttpContext context,
        string? instruments,
        IAdapterRegistry adapters,
        InstrumentMap map,
        DbSettings settings,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var wanted = ParseIds(instruments);
        if (wanted.Count == 0)
        {
            // Nothing asked for is a caller error, and it is reportable as a status because not one
            // byte of the stream has been written yet. Every failure past this line has to be words.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("instruments is required: /live?instruments=1,2,3", ct);
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var log = loggers.CreateLogger(typeof(LiveEgress));
        var conflator = new Conflator();
        var lastBeat = DateTimeOffset.MinValue;
        var seq = 0L;

        await context.Response.WriteAsync(": connected\n\n", ct);
        await context.Response.Body.FlushAsync(ct);

        using var ticker = new PeriodicTimer(Tick);
        try
        {
            while (await ticker.WaitForNextTickAsync(ct))
            {
                var snapshot = await map.CurrentAsync(ct);
                // Both of these are TTL-guarded reads of a cached value, not queries — and both are
                // read per tick rather than once, because an instrument switched on or a staleness
                // threshold changed in the console must reach the live path without a restart.
                var maxAge = (await settings.CurrentAsync(ct)).WsStaleAfter;
                var polled = Poll(wanted, snapshot, adapters, maxAge);
                var changed = conflator.Changed(polled);

                if (changed.Count > 0)
                {
                    seq++;
                    var payload = JsonSerializer.Serialize(changed.Select(c => Wired(c.InstrumentId, snapshot, c.Quote)), Wire);
                    await context.Response.WriteAsync($"event: quotes\nid: {seq.ToString(CultureInfo.InvariantCulture)}\ndata: {payload}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                    lastBeat = DateTimeOffset.UtcNow;
                    continue;
                }

                // A market that is not moving and a hub that has died look the same down the wire,
                // so the quiet one says so. Timed off the last WRITE, not the last tick: a frame is
                // itself proof of life, and a comment on top of it would be noise 5 times a second.
                if (DateTimeOffset.UtcNow - lastBeat >= Heartbeat)
                {
                    await context.Response.WriteAsync(": ping\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                    lastBeat = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The studio closed the connection, or the hub is shutting down. Neither is an error and
            // there is nothing left to report it to.
        }
        catch (IOException)
        {
            // The same event arriving the other way — a write onto a reset connection.
        }
        catch (Exception ex)
        {
            // Past the first byte the status is long since sent, so a failure is reported in words
            // and the connection ends; the studio reconnects with backoff and falls back to the
            // database meanwhile, which is exactly the degraded path it is built to survive.
            log.LogWarning(ex, "Live egress failed for {Count} instruments", wanted.Count);
        }
    }

    /// <summary>Every wanted instrument its segment's socket currently holds. Walks segments, not
    /// instruments: one <see cref="Connectors.IExchangeMarketData.LiveQuotes"/> call answers for a
    /// whole venue, so asking per instrument would be the same poll repeated per row.</summary>
    private static List<(int InstrumentId, LiveQuote Quote)> Poll(
        HashSet<int> wanted,
        InstrumentMap.Snapshot map,
        IAdapterRegistry adapters,
        TimeSpan maxAge)
    {
        var found = new List<(int, LiveQuote)>();
        foreach (var segment in map.SegmentsOf(wanted))
        {
            if (!adapters.TryGet(segment, out var adapter))
            {
                // The exchange is not running — its rows stay on the database, which is the same
                // answer as a socket that carries nothing.
                continue;
            }

            foreach (var quote in adapter.LiveQuotes(maxAge))
            {
                if (map.TryGetId(segment, quote.ExchangeSymbol, out var id) && wanted.Contains(id))
                {
                    found.Add((id, quote));
                }
            }
        }

        return found;
    }

    private static WireQuote Wired(int instrumentId, InstrumentMap.Snapshot map, LiveQuote q) =>
        new(instrumentId,
            map.TryGetSegment(instrumentId, out var segment) ? segment : string.Empty,
            q.At.ToUnixTimeMilliseconds(),
            q.BidPrice, q.BidSize, q.AskPrice, q.AskSize,
            q.LastPrice, q.MarkPrice, q.IndexPrice, q.FundingRate,
            q.OpenInterest, q.Turnover24h,
            q.Depth is { } d ? new WireDepth(d.Bid10Bps, d.Ask10Bps, d.Bid25Bps, d.Ask25Bps, d.Bid50Bps, d.Ask50Bps, d.Mid) : null);

    /// <summary>The frame's shape. Short names because this rides five times a second; nulls are
    /// dropped entirely rather than written, so "this venue has no mark price" costs nothing on the
    /// wire and stays distinguishable from a zero.</summary>
    private sealed record WireQuote(
        [property: JsonPropertyName("id")] int InstrumentId,
        [property: JsonPropertyName("seg")] string Segment,
        [property: JsonPropertyName("at")] long AtUnixMs,
        [property: JsonPropertyName("bid")] double? BidPrice,
        [property: JsonPropertyName("bidSz")] double? BidSize,
        [property: JsonPropertyName("ask")] double? AskPrice,
        [property: JsonPropertyName("askSz")] double? AskSize,
        [property: JsonPropertyName("last")] double? LastPrice,
        [property: JsonPropertyName("mark")] double? MarkPrice,
        [property: JsonPropertyName("index")] double? IndexPrice,
        [property: JsonPropertyName("fund")] double? FundingRate,
        [property: JsonPropertyName("oi")] double? OpenInterest,
        [property: JsonPropertyName("turn")] double? Turnover24h,
        [property: JsonPropertyName("depth")] WireDepth? Depth);

    private sealed record WireDepth(
        [property: JsonPropertyName("b10")] double? Bid10Bps,
        [property: JsonPropertyName("a10")] double? Ask10Bps,
        [property: JsonPropertyName("b25")] double? Bid25Bps,
        [property: JsonPropertyName("a25")] double? Ask25Bps,
        [property: JsonPropertyName("b50")] double? Bid50Bps,
        [property: JsonPropertyName("a50")] double? Ask50Bps,
        [property: JsonPropertyName("mid")] double? Mid);

    /// <summary>Ids from the query string, skipping anything that is not one rather than failing the
    /// whole request: a studio that has just seen a listing appear should not lose every other row
    /// to one id the hub cannot parse.</summary>
    internal static HashSet<int> ParseIds(string? instruments)
    {
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(instruments))
        {
            return ids;
        }

        foreach (var part in instruments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}

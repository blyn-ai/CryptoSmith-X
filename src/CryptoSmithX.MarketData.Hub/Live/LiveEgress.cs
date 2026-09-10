using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Streaming;
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

    /// <summary>How far behind one subscriber's tape may fall before it starts losing prints.
    ///
    /// The page shows a tape of about twenty rows, and this is drained five times a second, so a
    /// thousand is roughly ten seconds of a busy venue's flow — far more than a viewer can be behind
    /// and still be reading a tape rather than a history. It is a bound, not a target: what matters
    /// is that a viewer who stalls costs itself prints and costs the recorder none.
    /// </summary>
    private const int TapeCapacity = 1_000;

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
        // The connect line below is itself proof of life, so the heartbeat clock starts from it —
        // otherwise the first quiet tick fires a ping immediately, on a connection one tick old.
        var lastBeat = DateTimeOffset.UtcNow;
        var seq = 0L;

        await context.Response.WriteAsync(": connected\n\n", ct);
        await context.Response.Body.FlushAsync(ct);

        // One tape subscription per venue this connection watches, opened on the first tick that
        // knows which venues those are and closed with the connection. Per connection rather than
        // shared, because a tap is drained by whoever reads it: two connections sharing one would
        // take prints from each other exactly the way a second Drain() would take them from the
        // database.
        var tapes = new Dictionary<string, EventTap<TradeEvent>>(StringComparer.Ordinal);

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

                var wrote = false;
                if (changed.Count > 0)
                {
                    seq++;
                    var payload = JsonSerializer.Serialize(changed.Select(c => Wired(c.InstrumentId, snapshot, c.Quote)), Wire);
                    await context.Response.WriteAsync($"event: quotes\nid: {seq.ToString(CultureInfo.InvariantCulture)}\ndata: {payload}\n\n", ct);
                    wrote = true;
                }

                var prints = DrainTapes(wanted, snapshot, adapters, tapes);
                if (prints.Count > 0)
                {
                    seq++;
                    var payload = JsonSerializer.Serialize(prints, Wire);
                    await context.Response.WriteAsync($"event: trades\nid: {seq.ToString(CultureInfo.InvariantCulture)}\ndata: {payload}\n\n", ct);
                    wrote = true;
                }

                if (wrote)
                {
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
        finally
        {
            // A tape left subscribed after its reader is gone is a queue the socket keeps filling
            // for nobody, for the life of the process.
            foreach (var tape in tapes.Values)
            {
                tape.Dispose();
            }
        }
    }

    /// <summary>Every print since the last tick on the venues this connection watches, for the
    /// instruments it asked about. Subscribes on first sight of a venue: a tape is a live view, so
    /// it starts when someone starts watching and carries no backlog.</summary>
    private static List<WireTrade> DrainTapes(
        HashSet<int> wanted,
        InstrumentMap.Snapshot map,
        IAdapterRegistry adapters,
        Dictionary<string, EventTap<TradeEvent>> tapes)
    {
        var prints = new List<WireTrade>();
        foreach (var (segment, symbols) in map.WantedBySegment(wanted))
        {
            if (!tapes.TryGetValue(segment, out var tape))
            {
                if (!adapters.TryGet(segment, out var adapter) || adapter.ObserveTrades(TapeCapacity) is not { } opened)
                {
                    // No socket on this venue, or the exchange is not running: no tape, and the page
                    // keeps whatever the collectors recorded.
                    continue;
                }

                tapes[segment] = tape = opened;
            }

            foreach (var t in tape.Drain())
            {
                // The tap carries the whole venue's tape — it is the feed's buffer, and the feed
                // subscribes for its own reasons. The connection asked about a few listings.
                if (symbols.Contains(t.ExchangeSymbol) && map.TryGetId(segment, t.ExchangeSymbol, out var id))
                {
                    prints.Add(new WireTrade(
                        id, segment, t.EventTime.ToUnixTimeMilliseconds(),
                        t.Price, t.Qty, t.TakerSide, t.TradeType));
                }
            }
        }

        return prints;
    }

    /// <summary>Every wanted instrument its segment's socket currently holds. One call per venue,
    /// naming that venue's own symbols — never per instrument, and never for the whole listing; see
    /// <see cref="Connectors.IExchangeMarketData.LiveQuotes"/> for what the whole-listing version
    /// cost.</summary>
    private static List<(int InstrumentId, LiveQuote Quote)> Poll(
        HashSet<int> wanted,
        InstrumentMap.Snapshot map,
        IAdapterRegistry adapters,
        TimeSpan maxAge)
    {
        var found = new List<(int, LiveQuote)>(wanted.Count);
        foreach (var (segment, symbols) in map.WantedBySegment(wanted))
        {
            if (!adapters.TryGet(segment, out var adapter))
            {
                // The exchange is not running — its rows stay on the database, which is the same
                // answer as a socket that carries nothing.
                continue;
            }

            foreach (var quote in adapter.LiveQuotes(symbols, maxAge))
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

    /// <summary>One print. No venue uid and no sequence: those exist so the RECORD can be
    /// de-duplicated and ordered, and this is a view that is allowed to miss prints entirely —
    /// carrying an identity would invite the page to pretend otherwise.</summary>
    private sealed record WireTrade(
        [property: JsonPropertyName("id")] int InstrumentId,
        [property: JsonPropertyName("seg")] string Segment,
        [property: JsonPropertyName("at")] long AtUnixMs,
        [property: JsonPropertyName("px")] double Price,
        [property: JsonPropertyName("qty")] double Qty,
        [property: JsonPropertyName("side")] string TakerSide,
        [property: JsonPropertyName("kind")] string? TradeType);

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

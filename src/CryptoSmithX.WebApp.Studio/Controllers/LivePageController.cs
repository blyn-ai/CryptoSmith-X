using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Live;
using CryptoSmithX.WebApp.Studio.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CryptoSmithX.WebApp.Studio.Controllers;

/// <summary>
/// The live stream, shared by both designs of the asset page.
///
/// It lives here rather than on one controller because there are now TWO pages that can be watched
/// and the stream is the delicate part: a slot gate that must always be given back, a debounce that
/// folds a burst of collectors into one render, a signal that has to tell "the announcements are
/// dead" apart from "the announcements arrive and the page cannot be rebuilt", and a heartbeat that
/// keeps proxies from closing an idle connection. Copied for the second page, every one of those is
/// a second place to fix and a second place to get subtly wrong — and the failures are silent ones:
/// a stream that never reports itself down looks exactly like a market that went quiet.
///
/// THE PAGES DIFFER IN EXACTLY TWO THINGS and they are the two abstract members below: which
/// fragments they redraw, and how they load. Everything else is identical because it is the same
/// mechanism, not a similar one.
/// </summary>
public abstract class LivePageController : Controller
{
    protected LivePageController(
        LiveNotifier notifier,
        LiveStreamGate streams,
        ICompositeViewEngine viewEngine,
        ILogger logger)
    {
        Notifier = notifier;
        Streams = streams;
        ViewEngine = viewEngine;
        Logger = logger;
    }

    protected LiveNotifier Notifier { get; }

    protected LiveStreamGate Streams { get; }

    protected ICompositeViewEngine ViewEngine { get; }

    protected ILogger Logger { get; }

    /// <summary>The page, loaded the same way the first paint loads it — through the same cache key,
    /// so a stream and a reload of the tab beside it cannot disagree about the market.</summary>
    protected abstract Task<PairPageModel?> LoadPageAsync(string baseFamily, CancellationToken ct);

    /// <summary>
    /// The live upgrade of the pair page: Server-Sent Events, opened only when the reader presses
    /// the button.
    ///
    /// <b>It re-renders the same partials the first paint used</b> — that is the whole design and
    /// everything else here is in service of it. The rules that decide where a dash goes, which end
    /// of a column is marked and how far a figure has faded ran in <c>RowCells</c> and
    /// <c>Verdicts</c> before either path reached a view, and there is exactly one set of them. A
    /// second renderer — JSON out of here and cells assembled in JavaScript — would be a second
    /// place for those rules to live, and the second place is the one nobody reads and CI cannot
    /// see (blueprint §1).
    ///
    /// <b>SSE and not WebSocket</b>, for the reason the admin console gives: the page only receives,
    /// and reconnect and proxy-friendliness come for free. <b>SSE and not a poll</b> because a poll
    /// is a page that asks a question every N seconds whether or not anything happened; this asks
    /// nothing and is told.
    ///
    /// <b>What it does not do:</b> redraw the candle panels. They are hourly bars owned by the chart
    /// library, and pulling them out from under a reader who is panning one — every few seconds, for
    /// an update that cannot have changed them — would be the page moving for its own sake.
    /// </summary>
    [HttpGet]
    public async Task Live(string baseFamily, CancellationToken ct)
    {
        // Same check as Pair, and here it matters more rather than less — Program.cs says so about
        // the route constraint and it is just as true of the address that bypasses it: what this
        // endpoint hands out is a connection held open, and until this line existed an anonymous
        // caller could open one on any string at all through /studio/Pairs/Live?baseFamily=….
        if (!PairAddress.IsFamily(baseFamily))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Everything below writes an event stream, so the status code is settled here, before the
        // first byte, and never again. An error after the headers are out cannot be reported as a
        // status — it has to be reported in words, which is what the notice event is for.
        var model = await LoadPageAsync(baseFamily, ct);
        if (model is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // proxies that honour it: do not buffer this
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        if (!Streams.TryEnter())
        {
            // The refusal is a sentence, not a silence and not a 503: the browser retries a failed
            // status on its own, forever, which would turn a full feed into a queue of reconnects.
            // Told in words, the page closes the stream itself and says why.
            await WriteNoticeAsync("full", ct);
            return;
        }

        try
        {
            await Response.WriteAsync(": connected\n\n", ct);
            await Response.Body.FlushAsync(ct);

            // The set of segments this page is about. Reassigned after every push, because the row
            // set is not fixed — discovery can list this pair on a venue that was not here when the
            // tab was opened. The reference is read on the notifier's thread and written on this
            // one; a reference assignment is atomic and a push one pass late on a brand new venue is
            // not worth a lock on the notification path.
            var segments = SegmentsOf(model);

            // Bounded release with a CurrentCount check rather than a semaphore of one: five
            // collectors finishing inside the same second must fold into one render, and releasing
            // a semaphore that is already at its maximum throws.
            var pending = new SemaphoreSlim(0, int.MaxValue);
            using var subscription = Notifier.Subscribe(e =>
            {
                // A state change (Segment null) wakes the loop too — that is how a reader learns
                // the database signal died without waiting for the next heartbeat.
                if (e.Segment is null || LiveRelevance.Matters(e, segments))
                {
                    if (pending.CurrentCount == 0)
                    {
                        pending.Release();
                    }
                }
            });

            // A connection in the act of opening is not a connection that is down, and the two must
            // not read the same. This stream is the first subscriber whenever the reader is the only
            // one watching, so the LISTEN connection is opening as this line runs and will report
            // itself a fraction of a second later — long enough for the reader to be told the signal
            // is dead and then told it is fine, which is a page crying wolf on its own start-up. So
            // the answer is waited for, briefly, and reported once.
            //
            // Bounded, and the bound is short: if the database really is unreachable this returns
            // after a second and the page says so, which is the whole point of saying it.
            for (var i = 0; i < 20 && !Notifier.Listening && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(50, ct);
            }

            // Three states, not two, and the third one was found by pulling the database out from
            // under an open stream. "Down" is the signal: nothing will be announced. "Stalled" is
            // the other half of the same failure — the announcement arrives and the page cannot be
            // rebuilt, because the query behind it failed. Both leave the reader looking at figures
            // that are no longer being replaced, and a stream that stayed silent about either would
            // be showing a still page under the word "live".
            var stalled = false;
            string Signal() => !Notifier.Listening ? "down" : stalled ? "stalled" : "up";

            var signal = Signal();
            await WriteSignalAsync(signal, ct);

            // Sent whenever it changes and never otherwise: a reader is told that something about
            // this stream is different, not reminded every twenty-five seconds that it is fine.
            async Task SyncSignalAsync()
            {
                if (Signal() != signal)
                {
                    signal = Signal();
                    await WriteSignalAsync(signal, ct);
                }
            }

            // The opening push. Everything the reader is looking at was rendered before this stream
            // existed, so it is sent the current state rather than left to wait for a pass — and
            // what is sent is loaded HERE, after the wait above, not the model this action loaded to
            // find out whether the pair exists.
            //
            // The queue is drained first and the load happens second, in that order. Draining after
            // the load would lose a pass that landed in between; draining before it can only cost a
            // redundant push, because a pass that lands from now on is either already in this load
            // or still queued behind it. Given the choice between showing an update twice and not
            // showing it at all, this page shows it twice.
            while (pending.Wait(0))
            {
                // Signals from the connection setting itself up. What they would have asked for is
                // exactly what the load below produces.
            }

            var opening = await LoadPageAsync(baseFamily, ct) ?? model;
            segments = SegmentsOf(opening);
            await PushAsync(opening, ct);

            while (!ct.IsCancellationRequested)
            {
                // 25 s, under the 30 s idle timeout proxies commonly take: a comment line keeps the
                // connection from being reaped while saying nothing about the market.
                using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, heartbeat.Token);

                var woken = true;
                try
                {
                    await pending.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    woken = false;
                }

                // Reported before anything else on every pass through the loop, awake or idle. A
                // stream whose source of events has gone is the failure this whole surface is about:
                // it looks exactly like a market where nothing is happening, and it must not be
                // allowed to.
                await SyncSignalAsync();

                if (!woken)
                {
                    await Response.WriteAsync(": ping\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    continue;
                }

                // Past here the loop renders and pushes, and it does so for a signal that came BACK
                // as well as for a pass — deliberately. While the signal was down this stream was
                // told about nothing, so the page in front of the reader is as old as the outage;
                // the first thing a recovered connection owes them is the current state, not the
                // next pass whenever it happens to land.

                // Folds the rest of a burst — the other collectors of the same pass, a policy save
                // touching several rows — into the one render below.
                try
                {
                    await Task.Delay(DebounceMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                PairPageModel? next;
                try
                {
                    next = await LoadPageAsync(baseFamily, ct);
                    stalled = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The query failed: a database that went away, a pool with nothing left in it, a
                    // statement that timed out. The stream stays open — the failure may well be over
                    // by the next pass, and dropping the connection would cost a reconnect for a
                    // blip — but it stops claiming to be replacing anything, and the next iteration
                    // sends that as a signal. Cached failures were already ruled out one layer down
                    // (StudioCache), so the retry is a real retry.
                    Logger.LogWarning(ex, "Studio live: rebuilding {Asset} failed", baseFamily);
                    stalled = true;
                    await SyncSignalAsync();
                    continue;
                }

                if (next is null)
                {
                    // The pair stopped being listed under an open tab. Said out loud and the stream
                    // ends: an empty table would be a claim about the market, and going quiet would
                    // be worse than either.
                    await WriteNoticeAsync("gone", ct);
                    break;
                }

                segments = SegmentsOf(next);
                await SyncSignalAsync();
                await PushAsync(next, ct);

                // A floor equal to the cache's own window. Pushing faster than the cache can change
                // is sending the same HTML twice, and the second copy costs a render here and a
                // patched table under the reader's cursor there.
                try
                {
                    await Task.Delay(MinPushInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The reader closed the tab. Not an error, and nothing to report to a socket that is
            // already gone.
        }
        catch (IOException)
        {
            // The same event as above, arriving the other way: a write that failed because the
            // connection was reset rather than a token that was cancelled first. Also not an error,
            // and there is nowhere left to report it to.
        }
        finally
        {
            Streams.Exit();
        }
    }

    /// <summary>How long a burst is allowed to keep arriving before it is drawn as one update.</summary>
    private const int DebounceMs = 400;

    private static HashSet<string> SegmentsOf(PairPageModel model) =>
        model.Rows.Select(r => r.Row.SegmentCode).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// One update: the three regions of the page that are made of figures, then the clock.
    ///
    /// The clock goes LAST and it is not decoration. The fragments carry absolute instants and ages
    /// computed at the moment they were rendered; the client re-anchors on this instant and takes
    /// the ages back over from there. Sending it first would anchor the reader's clock to a page
    /// state that had not arrived yet.
    /// </summary>
    private async Task PushAsync(PairPageModel model, CancellationToken ct)
    {
        // The layout reads these three from ViewData, so the stamps partial is given them the same
        // way it is given them on the first paint — the alternative being a second model shape for
        // the same two timestamps.
        ViewData["CollectedFrom"] = model.CollectedFrom;
        ViewData["CollectedTo"] = model.CollectedTo;
        ViewData["RenderedAt"] = model.RenderedAt;
        ViewData["ShowCollected"] = true;

        foreach (var (region, view) in LiveRegions)
        {
            if (!Changed(region, model))
            {
                continue;
            }

            await WriteEventAsync("panel", region, await RenderPartialAsync(view, model), ct);
        }

        await WriteEventAsync(
            "clock",
            id: null,
            model.RenderedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ct);
    }

    /// <summary>
    /// What state this stream is actually in: <c>up</c>, <c>down</c> — nothing will be announced,
    /// the LISTEN connection is gone — or <c>stalled</c>, the announcements arrive and the page
    /// cannot be rebuilt.
    ///
    /// This is the difference between "nothing is happening" and "we have stopped being told", and
    /// the page says which in the reader's language. An open socket is not evidence of a live feed.
    /// </summary>
    private Task WriteSignalAsync(string state, CancellationToken ct) =>
        WriteEventAsync("signal", id: null, state, ct);

    private Task WriteNoticeAsync(string reason, CancellationToken ct) =>
        WriteEventAsync("notice", id: null, reason, ct);

    /// <summary>
    /// The regions of the pair page that a pass can change, each one the partial that drew it the
    /// first time. The candle panels are absent on purpose — see the note on <see cref="Live"/>.
    /// </summary>
    protected abstract (string Region, string View)[] LiveRegions { get; }

    /// <summary>
    /// Whether this region is worth sending on this pass. True by default — a collector wrote
    /// something, and the page is one reading.
    ///
    /// A page overrides it where one region answers to ONE call: replacing a book because the
    /// funding rate moved patches the element the reader's cursor is inside, several times a
    /// minute, for a picture that did not change.
    /// </summary>
    protected virtual bool Changed(string region, PairPageModel model) => true;

    /// <summary>
    /// The floor between two pushes.
    ///
    /// The cache's own window by default, which is the rate at which a new render can differ at
    /// all. A page overrides it upward when its fragments are LARGE and its reader is slow: nobody
    /// reads seven venues by twenty-six fields twice a second, and sending it to them anyway is a
    /// hundred kilobytes and a re-morph of the whole table for a change they will not have finished
    /// noticing before the next one lands.
    /// </summary>
    protected virtual TimeSpan MinPushInterval => StudioCache.Ttl;

    /// <summary>Renders a partial to a string outside the normal action-result pipeline, on this
    /// request's own <see cref="ControllerContext"/> so view lookup resolves exactly as
    /// <c>&lt;partial&gt;</c> does from the page.</summary>
    private async Task<string> RenderPartialAsync(string viewName, object model)
    {
        var viewResult = ViewEngine.FindView(ControllerContext, viewName, isMainPage: false);
        if (!viewResult.Success)
        {
            throw new InvalidOperationException($"View '{viewName}' not found for the live stream.");
        }

        await using var writer = new StringWriter();
        var viewData = new ViewDataDictionary(ViewData) { Model = model };
        var viewContext = new ViewContext(ControllerContext, viewResult.View, viewData, TempData, writer, new HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    /// <summary>
    /// One SSE frame. Each line of the payload needs its own <c>data:</c> prefix per the spec, and a
    /// rendered fragment is always many lines.
    /// </summary>
    /// <param name="id">
    /// The region the fragment belongs to, carried in the event's id so one handler on the client
    /// can place any fragment without knowing the list. It also becomes the browser's
    /// <c>Last-Event-ID</c> on reconnect, which this endpoint ignores: every stream starts by
    /// sending the current state, so there is no history to resume.
    /// </param>
    private async Task WriteEventAsync(string name, string? id, string payload, CancellationToken ct)
    {
        await Response.WriteAsync($"event: {name}\n", ct);
        if (id is not null)
        {
            await Response.WriteAsync($"id: {id}\n", ct);
        }

        foreach (var line in payload.Split('\n'))
        {
            await Response.WriteAsync($"data: {line.TrimEnd('\r')}\n", ct);
        }

        await Response.WriteAsync("\n", ct);
        await Response.Body.FlushAsync(ct);
    }

}

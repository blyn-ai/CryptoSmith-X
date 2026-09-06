using CryptoSmithX.Studio.Data;
using CryptoSmithX.Studio.Live;
using CryptoSmithX.Studio.Models;
using CryptoSmithX.Database;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CryptoSmithX.Studio.Controllers;

/// <summary>
/// The public showcase. Anonymous by construction — there is no authentication in this application
/// at all, so there is no [AllowAnonymous] here either: an attribute guarding against a policy that
/// does not exist reads as if one might.
/// </summary>
public sealed class PairsController : Controller
{
    private readonly Db _db;
    private readonly StudioCache _cache;
    private readonly TimeProvider _clock;
    private readonly LiveNotifier _notifier;
    private readonly LiveStreamGate _streams;
    private readonly ICompositeViewEngine _viewEngine;
    private readonly ILogger<PairsController> _logger;

    public PairsController(
        Db db,
        StudioCache cache,
        TimeProvider clock,
        LiveNotifier notifier,
        LiveStreamGate streams,
        ICompositeViewEngine viewEngine,
        ILogger<PairsController> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _notifier = notifier;
        _streams = streams;
        _viewEngine = viewEngine;
        _logger = logger;
    }

    /// <summary>The list of pairs, and the site's front door: PathBase makes this /studio.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        var search = (q ?? "").Trim();

        // The search term is part of the cache key because it is part of the answer, and it is the
        // one part an anonymous caller writes. What bounds the TABLE is the cache itself — a ceiling
        // on entries and a ceiling on key length, both argued on StudioCache — and NOT, as this
        // comment used to claim, a hope that "a term long enough to be interesting is a term nobody
        // is hammering". Nothing made that true: `maxlength="32"` lives on the HTML input and an
        // input element is not a server-side rule, so 30,000 requests with distinct 7.8 KB terms
        // grew this dictionary past two gigabytes and the container was OOM-killed.
        //
        // THE TERM'S OTHER COST IS NOT BOUNDED, and this comment used to read as though it were.
        // The string below becomes an `ilike '%…%'` pattern evaluated against every surviving row of
        // exchange_instrument, on two columns (StudioStore.PairsSql), and because a distinct term is
        // a guaranteed cache miss by construction that work is paid on every request rather than
        // once a second. Measured against 1,518 instruments: a three-character term answers in
        // 5.5-8.8 ms and a 7,800-character one — under Kestrel's 8 KB request-line limit, so nothing
        // rejects it — in 121-128 ms, and 30,000 of those at concurrency 32 held the whole surface
        // to 41-52 rps. Production scale in the blueprint is 20,005 instruments. The only thing
        // standing between that and an anonymous caller today is the request line's own length.
        //
        // Capping the term's LENGTH is still refused, and the reason is not memory — the cache has
        // that covered. It is that a filter answering a 40-character question with the results of
        // its first 32 characters is a page quietly showing something other than what was asked,
        // which is the failure this whole surface is built against. The term goes to the query
        // exactly as it arrived, and it is simply not remembered.
        //
        // Rejected, and it is the one that would have closed the cost honestly: answering an
        // over-long term with an empty result WITHOUT a query, on the grounds that no family code
        // can contain it. Nothing truthful is lost when the term genuinely cannot match — but
        // "cannot match" needs a maximum family length, and the schema does not have one:
        // `base_asset` is `text` (0001), and PairAddress's sixteen characters are a rule about what
        // may be ADDRESSED, not about what may be listed. A board that answered "nothing matches
        // that" for a term a longer code would have matched is the same lie as truncating it, made
        // quieter. Closing this properly means bounding the column in the schema or matching on a
        // prefix an index can serve, and both are changes to 0001's data model rather than to a
        // controller.
        var pairs = await _cache.GetAsync(
            "pairs:" + search,
            async token =>
            {
                await using var conn = await _db.OpenAsync(token);
                return await StudioStore.ListPairsAsync(conn, search, token);
            },
            ct);

        // Rendered-at is read here, per request, and never comes out of the cache. The list above
        // may be a second old; this is not, and the header says both.
        return View(new PairListModel(pairs.Items, pairs.Matching, pairs.Limit, search, _clock.GetUtcNow()));
    }

    /// <summary>
    /// One pair across every venue that lists it. Reached at /studio/BTC/USD — two bare segments,
    /// routed BELOW the default route (blueprint §9, and the argument is in Program.cs).
    /// </summary>
    /// <remarks>
    /// The two segments are passed to the query exactly as they arrive, with no case folding.
    /// 0024 says so directly: <c>asset_family_member.asset_code</c> holds the code "ровно в том
    /// написании, в каком он лежит в exchange_instrument… сравнение точное, поэтому регистр
    /// значим". Upper-casing here would be this layer inventing a normalisation the schema
    /// deliberately does not have, and it would quietly succeed on today's data — every code
    /// happens to be upper case — right up until the first venue lists an asset that is not.
    ///
    /// A pair nobody lists is a 404 with a page that says which pair, rather than an empty table.
    /// An empty table would be a claim about the market; this is a claim about the page.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> Pair(string baseFamily, string quoteFamily, CancellationToken ct)
    {
        // The route constraint is not what makes this safe, because this action is also reachable as
        // /studio/Pairs/Pair?baseFamily=… through the default route, where no constraint applies. The
        // rule is checked here, at the action, so it holds for every address that reaches it. The
        // full argument is on PairAddress.
        if (!PairAddress.IsFamily(baseFamily) || !PairAddress.IsFamily(quoteFamily))
        {
            return NotFound();
        }

        var model = await LoadAsync(baseFamily, quoteFamily, ct);
        if (model is null)
        {
            ViewData["MissingPair"] = baseFamily + "/" + quoteFamily;
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("PairNotFound");
        }

        return View(model);
    }

    /// <summary>
    /// The pair page, assembled. Called by the first paint and by every push on the live stream —
    /// one loader, so a figure cannot mean one thing when the page is opened and another thing when
    /// it is updated.
    /// </summary>
    private async Task<PairPageModel?> LoadAsync(string baseFamily, string quoteFamily, CancellationToken ct)
    {
        var data = await _cache.GetAsync<PairData?>(
            "pair:" + baseFamily + "/" + quoteFamily,
            async token =>
            {
                await using var conn = await _db.OpenAsync(token);
                var comparison = await StudioStore.GetPairAsync(conn, baseFamily, quoteFamily, token);
                if (comparison is null)
                {
                    return null;
                }

                var ids = comparison.Venues.Select(v => v.Row.InstrumentId).ToList();

                // Two series queries on one connection, both anchored to the same instant and both
                // resolved onto the same window list (CandleStore.Windows). Seven lines are drawn
                // per row from these two results and a reader compares them across the row, so they
                // have to be describing the same twenty-five hours — two anchors would put the
                // price line and the open-interest line an hour apart with nothing on the page to
                // say so.
                var at = _clock.GetUtcNow();
                var candles = await CandleStore.ReadAsync(conn, ids, at, token);
                var metrics = await MetricHourStore.ReadAsync(conn, ids, at, token);
                return new PairData(comparison, candles, metrics);
            },
            ct);

        if (data is null)
        {
            return null;
        }

        // Every age on the page is a subtraction against THIS instant — the time of the request —
        // and never against the moment the cache filled. The payload above holds only absolute
        // instants, which is what makes a second-old answer still able to report a truthful age
        // (blueprint §5). Doing the subtraction anywhere else would undo that.
        //
        // The live stream calls this method again per push for exactly the same reason: it must
        // never re-send a fragment it rendered a minute ago, because the ages baked into that
        // fragment were true a minute ago.
        var now = _clock.GetUtcNow();
        var comparison = data.Comparison;

        var rows = comparison.Venues
            .Select(v => new VenueRowModel(
                v.Row,
                v.Windows,
                new CallAges(
                    Freshness.AgeSeconds(v.Row.ReceivedAt, now),
                    Freshness.AgeSeconds(v.Row.OpenInterestAt, now),
                    Freshness.AgeSeconds(v.Row.DepthAt, now)),
                data.Candles.TryGetValue(v.Row.InstrumentId, out var c) ? c : CandleSeries.Empty,
                data.Metrics.TryGetValue(v.Row.InstrumentId, out var m) ? m : MetricHourSeries.Empty))
            .ToList();

        // The span the observations on this page actually cover, across all three calls on every
        // row. Both ends, and never the maximum alone: the freshest row on the page is not a
        // statement about the page, and a header printing it as one tells a reader that a table
        // holding a three-day-old venue is seconds old. Both are null — a dash in the header, not a
        // zero — when nothing here has ever been observed, which is a real state: discovery lists an
        // instrument the moment the venue announces it, and the first snapshot arrives later.
        var collected = PairPageModel.CollectedSpan(rows);

        // Computed HERE and not where the comparison was loaded, because a rank is now withheld
        // from a figure whose call has gone degraded and that is a judgement against `now`. Putting
        // it back in the cached payload would freeze the freshness half of it at the instant the
        // cache filled (blueprint §5, and the note on PairComparison).
        var verdicts = Verdicts.Compute(rows);

        return new PairPageModel(
            comparison.BaseFamily,
            comparison.QuoteFamily,
            rows,
            verdicts,
            ColumnScales.Compute(rows),
            collected.From,
            collected.To,
            now);
    }

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
    public async Task Live(string baseFamily, string quoteFamily, CancellationToken ct)
    {
        // Same check as Pair, and here it matters more rather than less — Program.cs says so about
        // the route constraint and it is just as true of the address that bypasses it: what this
        // endpoint hands out is a connection held open, and until this line existed an anonymous
        // caller could open one on any string at all through /studio/Pairs/Live?baseFamily=….
        if (!PairAddress.IsFamily(baseFamily) || !PairAddress.IsFamily(quoteFamily))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Everything below writes an event stream, so the status code is settled here, before the
        // first byte, and never again. An error after the headers are out cannot be reported as a
        // status — it has to be reported in words, which is what the notice event is for.
        var model = await LoadAsync(baseFamily, quoteFamily, ct);
        if (model is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // proxies that honour it: do not buffer this
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        if (!_streams.TryEnter())
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
            using var subscription = _notifier.Subscribe(e =>
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
            for (var i = 0; i < 20 && !_notifier.Listening && !ct.IsCancellationRequested; i++)
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
            string Signal() => !_notifier.Listening ? "down" : stalled ? "stalled" : "up";

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

            var opening = await LoadAsync(baseFamily, quoteFamily, ct) ?? model;
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
                    next = await LoadAsync(baseFamily, quoteFamily, ct);
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
                    _logger.LogWarning(ex, "Studio live: rebuilding {Pair} failed", baseFamily + "/" + quoteFamily);
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
                    await Task.Delay(StudioCache.Ttl, ct);
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
            _streams.Exit();
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
    private static readonly (string Region, string View)[] LiveRegions =
    [
        ("statement", "_Statement"),
        ("table", "_PairTable"),
        ("stamps", "_Stamps"),
    ];

    /// <summary>Renders a partial to a string outside the normal action-result pipeline, on this
    /// request's own <see cref="ControllerContext"/> so view lookup resolves exactly as
    /// <c>&lt;partial&gt;</c> does from the page.</summary>
    private async Task<string> RenderPartialAsync(string viewName, object model)
    {
        var viewResult = _viewEngine.FindView(ControllerContext, viewName, isMainPage: false);
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

    /// <summary>
    /// What the cache holds for one pair: the comparison and the hourly bars behind it, together,
    /// because they are fetched on one connection for one request and splitting them would double
    /// the round trips to halve nothing.
    ///
    /// It carries no "now" — deliberately, and that is the property the whole freshness model rests
    /// on. See <see cref="StudioCache"/>.
    /// </summary>
    private sealed record PairData(
        PairComparison Comparison,
        IReadOnlyDictionary<int, CandleSeries> Candles,
        IReadOnlyDictionary<int, MetricHourSeries> Metrics);
}

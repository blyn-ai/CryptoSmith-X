using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Live;
using Microsoft.AspNetCore.Http.Features;
using CryptoSmithX.WebApp.Studio.Models;
using CryptoSmithX.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;

namespace CryptoSmithX.WebApp.Studio.Controllers;

/// <summary>
/// The asset page, second design: /studio/v2/ARB.
///
/// It stands BESIDE the page at /studio/ARB rather than replacing it, and both are served for as
/// long as the new one is being judged. One address, one controller, one view — so the old page
/// keeps working untouched while this one changes under it, and neither has to carry a flag saying
/// which layout it is drawing.
///
/// THE MODEL IS THE SAME ONE. PairPageModel, the same rows, the same verdicts and scales, loaded by
/// the same query and through the same cache key: two presentations of one reading, never two
/// readings. A second loader would let the two pages disagree about the market at the same instant,
/// which on a site whose subject is "what did each venue say" is the one contradiction that cannot
/// be explained away.
/// </summary>
public sealed class PairsV2Controller : LivePageController
{
    private readonly Db _db;
    private readonly StudioCache _cache;
    private readonly TimeProvider _clock;
    private readonly LiveRooms _rooms;
    private readonly LiveFrameGate _frames;
    private readonly HubStream _hub;

    public PairsV2Controller(
        Db db,
        StudioCache cache,
        TimeProvider clock,
        LiveNotifier notifier,
        LiveStreamGate streams,
        LiveRooms rooms,
        LiveFrameGate frames,
        HubStream hub,
        ICompositeViewEngine viewEngine,
        ILogger<PairsV2Controller> logger)
        : base(notifier, streams, viewEngine, logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _rooms = rooms;
        _frames = frames;
        _hub = hub;
    }

    /// <summary>
    /// The live mode: this asset's room, joined, and its frames written out as they are computed.
    ///
    /// <b>No <c>panel</c> for the table.</b> Band 1 is carried by slots here, and sending the
    /// rendered partial as well would have <c>morph()</c> overwrite a live cell with the database's
    /// value — which the next tick would put back, two hundred milliseconds later, for as long as
    /// the tab stayed open. Band 2's panel is untouched; the books are not on the live path yet.
    ///
    /// <b>Its own gate.</b> A viewer here costs a room ticking five times a second, not a render on
    /// a collector pass, so it is counted against <see cref="LiveFrameGate"/> and not the Latest
    /// ceiling — and refused in words, never as a status, for the reason the Latest branch gives:
    /// a browser retries a failed status by itself, forever.
    /// </summary>
    protected override async Task LiveFramesAsync(string baseFamily, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var address = LiveFrameGate.AddressOf(HttpContext);
        if (!_frames.TryEnter(address))
        {
            await WriteAsync("event: notice\ndata: full\n\n", ct);
            return;
        }

        var room = _rooms.Room(baseFamily);
        var reader = room.Join();

        // A joining reader holds nothing yet, so the first tick owes it the whole picture rather
        // than a diff against a page it has not been sent.
        room.Resend();

        try
        {
            await WriteAsync(": connected\n\n", ct);
            await WriteAsync($"event: signal\ndata: {(_hub.State == HubStreamState.Down ? "degraded" : "up")}\n\n", ct);

            var beat = new PeriodicTimer(TimeSpan.FromSeconds(25));
            var heartbeat = beat.WaitForNextTickAsync(ct).AsTask();
            var next = reader.WaitToReadAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                var woke = await Task.WhenAny(next, heartbeat);
                if (ReferenceEquals(woke, heartbeat))
                {
                    // Under the 30 s idle timeout proxies commonly take. A comment line, so it keeps
                    // the connection alive while saying nothing about the market.
                    await WriteAsync(": ping\n\n", ct);
                    heartbeat = beat.WaitForNextTickAsync(ct).AsTask();
                    continue;
                }

                if (!await next)
                {
                    break;   // the room closed under us
                }

                next = reader.WaitToReadAsync(ct).AsTask();
                while (reader.TryRead(out var frame))
                {
                    await WriteAsync(
                        $"event: slots\nid: {frame.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n"
                        + $"data: {System.Text.Json.JsonSerializer.Serialize(frame)}\n\n",
                        ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The reader closed the tab.
        }
        catch (IOException)
        {
            // The same event the other way round: a write onto a connection already reset.
        }
        finally
        {
            _rooms.Leave(room, reader);
            _frames.Exit(address);
        }
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await Response.WriteAsync(text, ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// What a pass can change on this page, and what it deliberately cannot.
    ///
    /// Two regions: the table and the "now" band. Both are figures, both are re-rendered from the
    /// same partials the first paint used.
    ///
    /// THE OTHER THREE BANDS ARE ABSENT ON PURPOSE. Band 3 is candles and hourly lines — the chart
    /// library owns those panels, and pulling them out from under a reader who is panning one, every
    /// few seconds, for bars that cannot have changed, is the page moving for its own sake. Band 4
    /// follows the venues on its own switch and at its own rate. Band 5 is coverage and breaks,
    /// which move on the hour, not on a pass.
    /// </summary>
    protected override (string Region, string View)[] LiveRegions { get; } =
    [
        ("table", "_V2Table"),
        ("now", "_V2Now"),
    ];

    protected override Task<PairPageModel?> LoadPageAsync(string baseFamily, CancellationToken ct) =>
        PairPageLoader.LoadAsync(_db, _cache, _clock, baseFamily, ct);

    /// <summary>
    /// Five seconds between pushes, not one.
    ///
    /// Measured before choosing it: fifteen seconds of this stream carried nine pushes and 1.6 MB —
    /// the whole table nine times, to one reader, for figures that move in the fourth decimal. At
    /// that rate the page is redrawing faster than anyone can read a row of it, which is what a
    /// reader means by "it keeps refreshing".
    ///
    /// Nothing goes stale by waiting: the ages on screen keep counting on their own between pushes,
    /// which is the whole point of them being computed against a clock rather than baked in.
    /// </summary>
    protected override TimeSpan MinPushInterval => TimeSpan.FromSeconds(5);

    /// <summary>The newest depth observation this stream has already drawn, so band 2 is not
    /// replaced for a pass that did not touch it. One field, read and written on the one loop that
    /// owns this response.</summary>
    private DateTime? _sentDepthAt;

    /// <summary>
    /// Band 2 is the books, and the books answer to ONE call: the depth sweep. A ticker pass every
    /// two seconds would otherwise re-render twenty-five levels a side that nobody touched — under
    /// the cursor of a reader who is reading down them, which is the one thing that band is for.
    ///
    /// The table is the opposite case and is always sent: it carries every call on the page, so a
    /// pass that changed anything changed something in it.
    /// </summary>
    protected override bool Changed(string region, PairPageModel model)
    {
        if (region != "now")
        {
            return true;
        }

        var newest = model.Rows.Select(r => r.Row.DepthAt).Where(d => d is not null).DefaultIfEmpty(null).Max();
        if (newest is not null && newest == _sentDepthAt)
        {
            return false;
        }

        _sentDepthAt = newest;
        return true;
    }

    [HttpGet]
    public async Task<IActionResult> Asset(string baseFamily, short? tf, string? series, CancellationToken ct)
    {
        if (!PairAddress.IsFamily(baseFamily))
        {
            return NotFound();
        }

        var model = await PairPageLoader.LoadAsync(_db, _cache, _clock, baseFamily, ct);
        if (model is null)
        {
            ViewData["MissingPair"] = baseFamily;
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("~/Views/Pairs/PairNotFound.cshtml");
        }

        model = await ResolveBand3Async(model, baseFamily, tf, series, ct);

        // B3: studio-v2-band3.js fetches this SAME action with this header rather than following
        // the link — swapping #band3-head and #band3-cuts in place instead of navigating, so the
        // rest of the page (the tape mid-follow, a scroll position, band 5's open disclosures)
        // survives a timeframe or series switch. A plain link still works with scripts off: the
        // href is this action's own real address, and without the header it renders the full page.
        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            // Plain name, not a ~/ path: RenderPartialAsync calls ViewEngine.FindView, which
            // resolves by the CONTROLLER-RELATIVE convention (the same one <partial name="..."/>
            // uses) rather than by path — exactly how _V2Table/_V2Now are already named for the
            // live-push loop above. A ~/Views/... path is what GetView wants, not FindView, and
            // fails there with "not found" even though the file is real.
            var head = await RenderPartialAsync("_V2Band3Head", model);
            var cuts = await RenderPartialAsync("_V2Band3Cuts", model);
            var url = Url.RouteUrl("asset-v2", new
            {
                controller = "PairsV2", action = "Asset", baseFamily,
                tf = model.Band3Timeframe, series = model.Band3Series,
            }) + "#band-time";

            return Json(new { head, cuts, url });
        }

        return View(model);
    }

    /// <summary>
    /// B3: resolves the requested timeframe/series against what is actually offered, and loads a
    /// second candle series only when one was asked for. Shared by <see cref="Asset"/>'s full-page
    /// and AJAX-fragment branches — both have to make exactly the same choice from the same two
    /// query parameters, or a switch made through one path could render differently from the same
    /// switch made through the other.
    /// </summary>
    private async Task<PairPageModel> ResolveBand3Async(
        PairPageModel model, string baseFamily, short? tf, string? series, CancellationToken ct)
    {
        var chosenTf = CandleStore.Timeframes.Contains(tf ?? CandleStore.TimeframeMinutes)
            ? tf ?? CandleStore.TimeframeMinutes : CandleStore.TimeframeMinutes;
        var chosenSeries = CandleStore.Series.Contains(series) ? series! : "trade";

        // The default view (60-minute, trade) is already sitting in model.Rows[*].Candles — band
        // 1's sparklines loaded it. Only fetch a second series when the reader actually asked for a
        // different one, so the common case costs nothing extra.
        if (chosenTf == CandleStore.TimeframeMinutes && chosenSeries == "trade")
        {
            return model with { Band3Timeframe = chosenTf, Band3Series = chosenSeries };
        }

        var ids = model.Rows.Select(r => r.Row.InstrumentId).ToList();
        var band3 = await _cache.GetAsync($"band3:{baseFamily}:{chosenTf}:{chosenSeries}",
            async token =>
            {
                await using var conn = await _db.OpenAsync(token);
                var at = _clock.GetUtcNow();
                return chosenSeries == "trade"
                    ? await CandleStore.ReadAsync(conn, ids, at, chosenTf, CandleStore.Hours, token)
                    : await PriceCandleStore.ReadAsync(
                        conn, ids, at, chosenTf, CandleStore.Hours, chosenSeries, token);
            }, ct);

        return model with { Band3Candles = band3, Band3Timeframe = chosenTf, Band3Series = chosenSeries };
    }

    /// <summary>
    /// The tape alone, as JSON, for the FOLLOW switch on band 4.
    ///
    /// It exists because the tape is the ONE thing on this page that is a stream rather than a
    /// reading: everything else is a set of figures taken at one instant and drawn together, and
    /// refreshing a single figure out of that set would put two instants on one screen with nothing
    /// to say which is which. Fills carry their own venue clock, so eighteen of them replaced whole
    /// are still eighteen fills that happened — no such contradiction to make.
    ///
    /// The ids come from the SAME cached payload the page was drawn from, so a poll costs one small
    /// query rather than the page's whole load; the fills themselves are read fresh, because a
    /// cached tape is a tape that has stopped.
    /// </summary>
    /// <summary>
    /// B2: the listing cell's disclosure — one instrument's identity, limits, status and the open
    /// version of its typed spec. Fetched on click, like A2/A3 on the pair board, because a spec has
    /// no call and no age; it does not belong in the same live-pushed payload as the figures that do.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> InstrumentDetail(int instrumentId, CancellationToken ct)
    {
        var detail = await _cache.GetAsync("instrument-detail:" + instrumentId,
            async token =>
            {
                await using var conn = await _db.OpenAsync(token);
                return await InstrumentDetailStore.DetailAsync(conn, instrumentId, token);
            }, ct);

        return detail is null ? NotFound() : PartialView("~/Views/PairsV2/_InstrumentDetail.cshtml", detail);
    }

    /// <summary>
    /// B4: band 5's coverage ROW, disclosed — every request the last 24 h made for this dataset,
    /// across every venue on the page. Fetched on click, like B2's instrument disclosure: a request
    /// has no call and no age of its own, so it does not belong in the live-pushed payload either.
    /// <paramref name="ids"/> is comma-separated instrument ids rather than a second page load — the
    /// view already has them from the model it just rendered.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> CoverageDetail(string ids, string dataset, CancellationToken ct)
    {
        var instrumentIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        var rows = await _cache.GetAsync($"coverage-detail:{dataset}:{string.Join(',', instrumentIds)}",
            async token =>
            {
                await using var conn = await _db.OpenAsync(token);
                return await V2Store.CoverageDetailAsync(conn, instrumentIds, dataset, token);
            }, ct);

        return PartialView("~/Views/PairsV2/_CoverageDetail.cshtml", rows);
    }

    [HttpGet]
    public async Task<IActionResult> Tape(string baseFamily, CancellationToken ct)
    {
        if (!PairAddress.IsFamily(baseFamily))
        {
            return NotFound();
        }

        var model = await PairPageLoader.LoadAsync(_db, _cache, _clock, baseFamily, ct);
        if (model is null)
        {
            return NotFound();
        }

        var venues = model.Rows.ToDictionary(r => r.Row.InstrumentId, r => r.Row.ExchangeName);

        await using var conn = await _db.OpenAsync(ct);
        var tape = await V2Store.TapeAsync(conn, [.. venues.Keys], 18, ct);

        // Prompt 2, U-7: the SIZE bar's p95 read from this same batch, so a FOLLOW poll and the
        // first paint agree on what p95 means — see the view's own note on why it is not "the
        // last 200 prints" the finding names (that population needs a query this endpoint does
        // not otherwise make).
        var sizeSample = tape.Select(t => t.Qty).OrderBy(q => q).ToList();
        var p95 = Format.Percentile(sizeSample, 0.95);

        return Json(tape.Select(t => new
        {
            listing = t.InstrumentId,
            at = t.EventTime.ToString("HH:mm:ss"),
            atMs = t.EventTime.ToString(".fff"),
            venue = Format.VenueCode(venues.TryGetValue(t.InstrumentId, out var name) ? name : "—"),
            venueTitle = venues.TryGetValue(t.InstrumentId, out var fullName) ? fullName : "—",
            side = t.TakerSide,
            sideLetter = t.TakerSide[..1].ToUpperInvariant(),
            price = Format.Num(t.Price, 6),
            qty = Format.Num(t.Qty, 0),
            sizePct = p95 is { } m && m > 0 ? Math.Min(100.0, t.Qty / m * 100.0) : 0,
            over = p95 is { } m2 && t.Qty > m2,
            loud = t.TradeType is "liquidation" or "partial_liquidation" or "termination",
        }));
    }
}

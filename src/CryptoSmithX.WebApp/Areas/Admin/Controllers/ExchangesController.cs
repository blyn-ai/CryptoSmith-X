using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using CryptoSmithX.WebApp.Live;
using CryptoSmithX.WebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// The integrations dashboard. Lifecycle (status, name, description) is edited here; connection
/// health and every number on screen are derived from collector_status and exchange_instrument
/// on each render — the exchange row never stores an observation.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class ExchangesController : Controller
{
    private readonly Db _db;
    private readonly LiveNotifier _notifier;
    private readonly ICompositeViewEngine _viewEngine;

    public ExchangesController(Db db, LiveNotifier notifier, ICompositeViewEngine viewEngine)
    {
        _db = db;
        _notifier = notifier;
        _viewEngine = viewEngine;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var exchanges = await ExchangeStore.ListAsync(conn, ct);
        return View(exchanges);
    }

    [HttpGet]
    public async Task<IActionResult> Details(string id, string? tab, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var details = await ExchangeStore.GetAsync(conn, id, ct);
        if (details is null)
        {
            return NotFound();
        }

        // overview by default (it holds the Lifecycle control, the only way to enable a venue);
        // settings shows the configuration form.
        ViewData["Tab"] = tab == "settings" ? "settings" : "overview";
        return View(details);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        string id, string? name, string? description,
        string? baseUrl, string? chartsUrl, string? wsUrl, string? quoteAssets, string? blacklist,
        string? snapshotIntervalS, string? candleIntervalS, string? discoveryIntervalMin,
        string? fundingIntervalMin, string? depthIntervalS, CancellationToken ct)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0)
        {
            TempData["Error"] = "Name is required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Empty = "use the global"; a non-empty value must be a positive integer.
        if (!TryInterval(snapshotIntervalS, out var snap) || !TryInterval(candleIntervalS, out var candle)
            || !TryInterval(discoveryIntervalMin, out var disc) || !TryInterval(fundingIntervalMin, out var fund)
            || !TryInterval(depthIntervalS, out var depth))
        {
            TempData["Error"] = "Intervals must be a positive whole number, or empty for the global.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var input = new ExchangeSaveInput(
            Code: id,
            Name: name,
            Description: description?.Trim(),
            BaseUrl: baseUrl?.Trim(),
            ChartsUrl: chartsUrl?.Trim(),
            WsUrl: wsUrl?.Trim(),
            QuoteAssets: SplitList(quoteAssets),
            Blacklist: SplitList(blacklist),
            SnapshotIntervalS: snap,
            CandleIntervalS: candle,
            DiscoveryIntervalMin: disc,
            FundingIntervalMin: fund,
            DepthIntervalS: depth,
            UpdatedBy: User.Identity?.Name);

        await using var conn = await _db.OpenAsync(ct);
        var saved = await ExchangeStore.SaveAsync(conn, input, ct);
        if (!saved)
        {
            TempData["Error"] = "Unknown exchange.";
            return RedirectToAction(nameof(Details), new { id, tab = "settings" });
        }

        TempData["Saved"] = "Saved.";
        return RedirectToAction(nameof(Details), new { id, tab = "settings" });
    }

    /// <summary>
    /// The guarded status change from the Lifecycle control. Status is deliberately not part of the
    /// settings form: this is the one control that stops collection, so it requires typing the
    /// exchange code to confirm, checked here as well as in the browser.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(string id, string? newStatus, string? confirmCode, CancellationToken ct)
    {
        if (!string.Equals(confirmCode?.Trim(), id, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Status unchanged — the typed code did not match.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await using var conn = await _db.OpenAsync(ct);
        var ok = await ExchangeStore.SetStatusAsync(conn, id, newStatus?.Trim() ?? "", User.Identity?.Name, ct);
        TempData[ok ? "Saved" : "Error"] = ok ? $"Saved. {id} is now {newStatus}." : "Unknown exchange or invalid status.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string[] SplitList(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryInterval(string? raw, out int? value)
    {
        value = null;
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            value = n;
            return true;
        }

        return false;
    }
    /// <summary>The policy save for one feed (Edit feed dialog). The loss guard — typing the
    /// dataset code to release a stop that would drop unrecoverable history — is enforced again
    /// server-side in <see cref="FeedStore.SaveAsync"/>, the same way <see cref="Status"/> guards the
    /// exchange code.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Feed(
        string id, string dataset, string mode, string? intervalS, string? historyIntervalS,
        string? retentionDays, string? transport, string? note, string? confirmCode, CancellationToken ct)
    {
        if (!TryInterval(intervalS, out var interval)
            || !TryInterval(historyIntervalS, out var historyInterval)
            || !TryInterval(retentionDays, out var retention))
        {
            TempData["Error"] = $"{dataset}: interval, keep interval and retention must be a positive whole number, or empty to inherit.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var input = new FeedSaveInput(
            SegmentCode: id,
            DatasetCode: dataset,
            Mode: mode,
            IntervalS: interval,
            HistoryIntervalS: historyInterval,
            RetentionDays: retention,
            Transport: string.IsNullOrWhiteSpace(transport) ? null : transport,
            Note: note?.Trim(),
            ConfirmCode: confirmCode,
            UpdatedBy: User.Identity?.Name);

        await using var conn = await _db.OpenAsync(ct);
        var error = await FeedStore.SaveAsync(conn, input, ct);
        TempData[error is null ? "Saved" : "Error"] = error ?? $"{dataset}: saved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Runs(string id, string? collector, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        ViewData["Code"] = id;
        ViewData["Collector"] = collector;
        return View(await RunStore.ListAsync(conn, id, collector, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Run(long id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var details = await RunStore.GetAsync(conn, id, ct);
        return details is null ? NotFound() : View(details);
    }

    /// <summary>
    /// The live feed for one exchange's overview tab: Server-Sent Events, not WebSocket — the page
    /// only receives, and SSE gets reconnect and proxy-friendliness for free. <see cref="LiveNotifier"/>
    /// already turns Postgres NOTIFY into an in-process event; this action just filters it to the
    /// exchange in the URL, debounces a burst of passes into one render, and re-renders the same
    /// partials Details.cshtml uses for its first paint — one template, not a second one reimplemented
    /// in JS. live.js applies the fragment to <c>[data-live-region]</c> and falls back to its 10 s poll
    /// whenever this stream is not open; it never assumes the stream is the only way the page updates.
    /// </summary>
    [HttpGet]
    public async Task Live(string id, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // upstream proxies that honour it: do not buffer this
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var pending = new SemaphoreSlim(0, int.MaxValue);
        void OnNotified(string segmentCode, string? collector)
        {
            if (string.Equals(segmentCode, id, StringComparison.Ordinal))
            {
                // A bounded release: a burst of five collectors finishing within the same second must
                // not queue five renders — one pending signal is all the debounce loop below needs.
                if (pending.CurrentCount == 0)
                {
                    pending.Release();
                }
            }
        }

        _notifier.Notified += OnNotified;
        try
        {
            await Response.WriteAsync(": connected\n\n", ct);
            await Response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, heartbeat.Token);
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
                    // Heartbeat fired, not the request — a comment line is enough to keep a proxy from
                    // deciding this idle connection is dead, without becoming a panel update itself.
                    await Response.WriteAsync(": ping\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    continue;
                }

                // A short debounce window folds the rest of a burst (the other collectors of the same
                // pass, a policy save that touches several rows) into the single render below.
                try
                {
                    await Task.Delay(400, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await PushPanelsAsync(id, ct);
            }
        }
        finally
        {
            _notifier.Notified -= OnNotified;
        }
    }

    private async Task PushPanelsAsync(string segmentCode, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var details = await ExchangeStore.GetAsync(conn, segmentCode, ct);
        if (details is null)
        {
            return; // the exchange was removed from under an open tab — nothing to render
        }

        foreach (var (region, view) in LiveRegions)
        {
            var html = await RenderPartialAsync(view, details);
            await WriteEventAsync(region, html, ct);
        }
    }

    private static readonly (string Region, string View)[] LiveRegions =
    [
        ("head-badge", "_ExchangeHeadBadge"),
        ("stat-row", "_ExchangeStatRow"),
        ("feeds-panel", "_DataFeedsPanel"),
        ("throughput-panel", "_ThroughputPanel"),
        ("latency-panel", "_LatencyPanel"),
        ("stalest-panel", "_StalestPanel"),
    ];

    /// <summary>Renders a partial view to a string outside the normal action-result pipeline, reusing
    /// this request's own <see cref="ControllerContext"/> so Area/Controller-relative view lookup
    /// resolves exactly as <c>Html.PartialAsync</c> would from Details.cshtml.</summary>
    private async Task<string> RenderPartialAsync(string viewName, object model)
    {
        var viewResult = _viewEngine.FindView(ControllerContext, viewName, isMainPage: false);
        if (!viewResult.Success)
        {
            throw new InvalidOperationException($"View '{viewName}' not found for live push.");
        }

        await using var writer = new StringWriter();
        var viewData = new ViewDataDictionary(ViewData) { Model = model };
        var viewContext = new ViewContext(ControllerContext, viewResult.View, viewData, TempData, writer, new HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    /// <summary>One SSE frame. Per spec each line of a multi-line payload needs its own <c>data:</c>
    /// prefix — a rendered panel is always multi-line HTML.</summary>
    private async Task WriteEventAsync(string region, string html, CancellationToken ct)
    {
        await Response.WriteAsync($"event: panel\nid: {region}\n", ct);
        foreach (var line in html.Split('\n'))
        {
            await Response.WriteAsync($"data: {line}\n", ct);
        }

        await Response.WriteAsync("\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}

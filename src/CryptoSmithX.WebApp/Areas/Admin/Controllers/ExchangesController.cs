using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using CryptoSmithX.WebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public ExchangesController(Db db) => _db = db;

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
    /// collection code to release a stop that would drop unrecoverable history — is enforced again
    /// server-side in <see cref="FeedStore.SaveAsync"/>, the same way <see cref="Status"/> guards the
    /// exchange code.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Feed(
        string id, string collection, string mode, string? intervalS, string? retentionDays,
        string? transport, string? note, string? confirmCode, CancellationToken ct)
    {
        if (!TryInterval(intervalS, out var interval) || !TryInterval(retentionDays, out var retention))
        {
            TempData["Error"] = $"{collection}: interval and retention must be a positive whole number, or empty to inherit.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var input = new FeedSaveInput(
            ExchangeCode: id,
            CollectionCode: collection,
            Mode: mode,
            IntervalS: interval,
            RetentionDays: retention,
            Transport: string.IsNullOrWhiteSpace(transport) ? null : transport,
            Note: note?.Trim(),
            ConfirmCode: confirmCode,
            UpdatedBy: User.Identity?.Name);

        await using var conn = await _db.OpenAsync(ct);
        var error = await FeedStore.SaveAsync(conn, input, ct);
        TempData[error is null ? "Saved" : "Error"] = error ?? $"{collection}: saved.";
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

}

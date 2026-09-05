using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Areas.Admin.Controllers;

/// <summary>
/// A trading pair across every platform that lists it: top of book as a table, candles as a chart
/// per platform underneath. The pair is the subject rather than the asset because the quote currency
/// is part of what is being compared — BTC/USD and BTC/USDT are two different prices, and putting
/// them in one table would invite subtracting them.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class PairsController : Controller
{
    private readonly Db _db;

    public PairsController(Db db) => _db = db;

    /// <summary>Timeframes the rollup actually produces. Anything else would render an empty chart
    /// that looks like missing data rather than a bad request.</summary>
    internal static readonly short[] Timeframes = [1, 5, 15, 60, 240, 720, 1440];

    [HttpGet]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        ViewData["Search"] = q;
        return View(await PairStore.ListAsync(conn, q, ct));
    }

    [HttpGet]
    public async Task<IActionResult> At(
        string id, string? quote, string? at, short? tf, int? n, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return RedirectToAction(nameof(Index));
        }

        // Parsed as UTC explicitly: a page whose whole point is which instant you are looking at
        // must not quietly reinterpret it in the server's local time.
        var moment = DateTime.TryParse(
            at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : DateTime.UtcNow;

        var timeframe = tf is { } t && Timeframes.Contains(t) ? t : (short)1;
        var windows = Math.Clamp(n ?? 90, 20, 240);

        await using var conn = await _db.OpenAsync(ct);
        var slice = await PairStore.AtAsync(conn, id, quote, moment, timeframe, windows, ct);
        return slice is null ? NotFound() : View(slice);
    }
}

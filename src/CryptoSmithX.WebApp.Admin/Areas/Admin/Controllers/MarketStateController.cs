using CryptoSmithX.Database;
using CryptoSmithX.WebApp.Admin.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Admin.Areas.Admin.Controllers;

/// <summary>
/// The market at one instant: rows are instruments, columns are what was measured, and every row
/// carries its own measurement times rather than borrowing the page's.
///
/// This exists because the run pages asked a question the data cannot answer. Rows carry no run id,
/// so "what did this pass produce" had to be reconstructed by time window — which came back empty
/// for roughly three snapshot passes in four and for every depth pass but the newest. "What did the
/// market look like at 12:04:31" needs no provenance at all and is what an operator is actually
/// asking when they open a run.
/// </summary>
[Area("Admin")]
[Authorize(Roles = "admin")]
public sealed class MarketStateController : Controller
{
    private readonly Db _db;

    public MarketStateController(Db db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Index(string? at, string? segment, CancellationToken ct)
    {
        // No moment given means the most recent one. Parsed as UTC explicitly: a page whose whole
        // point is which instant you are looking at must not quietly reinterpret it in the server's
        // local time.
        var moment = DateTime.TryParse(
            at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : DateTime.UtcNow;

        await using var conn = await _db.OpenAsync(ct);
        var slice = await MarketStateStore.AtAsync(conn, moment, string.IsNullOrWhiteSpace(segment) ? null : segment, ct);
        return View(slice);
    }
}

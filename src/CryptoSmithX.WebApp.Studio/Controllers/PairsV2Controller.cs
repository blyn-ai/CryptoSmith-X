using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;
using CryptoSmithX.Database;
using Microsoft.AspNetCore.Mvc;

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
public sealed class PairsV2Controller : Controller
{
    private readonly Db _db;
    private readonly StudioCache _cache;
    private readonly TimeProvider _clock;

    public PairsV2Controller(Db db, StudioCache cache, TimeProvider clock)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
    }

    [HttpGet]
    public async Task<IActionResult> Asset(string baseFamily, CancellationToken ct)
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

        return View(model);
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

        return Json(tape.Select(t => new
        {
            at = t.EventTime.ToString("HH:mm:ss.fff"),
            venue = venues.TryGetValue(t.InstrumentId, out var name) ? name : "—",
            side = t.TakerSide,
            price = Format.Num(t.Price, 6),
            qty = Format.Num(t.Qty, 0),
            kind = (t.TradeType ?? "fill").Replace("_", " ", StringComparison.Ordinal),
            loud = t.TradeType is "liquidation" or "partial_liquidation" or "termination",
        }));
    }
}

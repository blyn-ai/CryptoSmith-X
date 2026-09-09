using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Live;
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

    public PairsV2Controller(
        Db db,
        StudioCache cache,
        TimeProvider clock,
        LiveNotifier notifier,
        LiveStreamGate streams,
        ICompositeViewEngine viewEngine,
        ILogger<PairsV2Controller> logger)
        : base(notifier, streams, viewEngine, logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
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
            listing = t.InstrumentId,
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

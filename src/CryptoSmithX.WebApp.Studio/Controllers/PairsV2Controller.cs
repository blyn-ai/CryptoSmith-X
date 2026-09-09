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
}

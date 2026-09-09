using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Live;
using CryptoSmithX.WebApp.Studio.Models;
using CryptoSmithX.Database;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CryptoSmithX.WebApp.Studio.Controllers;

/// <summary>
/// The public showcase. Anonymous by construction — there is no authentication in this application
/// at all, so there is no [AllowAnonymous] here either: an attribute guarding against a policy that
/// does not exist reads as if one might.
/// </summary>
public sealed class PairsController : LivePageController
{
    private readonly Db _db;
    private readonly StudioCache _cache;
    private readonly TimeProvider _clock;

    public PairsController(
        Db db,
        StudioCache cache,
        TimeProvider clock,
        LiveNotifier notifier,
        LiveStreamGate streams,
        ICompositeViewEngine viewEngine,
        ILogger<PairsController> logger)
        : base(notifier, streams, viewEngine, logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
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
    /// <summary>
    /// The old two-segment address, /studio/PEPE/USD, kept alive as a redirect to /studio/PEPE.
    ///
    /// 302 and not 301: a permanent redirect is cached by the browser forever, and this address may
    /// yet come back as a quote filter on the asset page. The quote is dropped rather than carried
    /// into a query string, because the page's default is deliberately all quotes together and a
    /// link that silently narrowed it would defeat the reason the pages were merged.
    /// </summary>
    [HttpGet]
    public IActionResult Pair(string baseFamily, string quoteFamily)
    {
        if (!PairAddress.IsFamily(baseFamily) || !PairAddress.IsFamily(quoteFamily))
        {
            return NotFound();
        }

        // BY ROUTE NAME, never RedirectToAction. The default route is registered first, so
        // RedirectToAction resolves this to /studio/Pairs/Asset?baseFamily=PEPE — an address that
        // works and that nobody should ever be sent to, least of all by a redirect that then sits
        // in the reader's history and in every log. The same trap is already documented on the live
        // stream's link; this is the second place it bites.
        return RedirectToRoute("asset", new { baseFamily });
    }

    /// <summary>
    /// One base asset, every venue and every quote that lists it.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Asset(string baseFamily, CancellationToken ct)
    {
        // The route constraint is not what makes this safe, because this action is also reachable as
        // /studio/Pairs/Asset?baseFamily=… through the default route, where no constraint applies.
        // The rule is checked here, at the action, so it holds for every address that reaches it.
        // The full argument is on PairAddress.
        if (!PairAddress.IsFamily(baseFamily))
        {
            return NotFound();
        }

        var model = await LoadAsync(baseFamily, ct);
        if (model is null)
        {
            ViewData["MissingPair"] = baseFamily;
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("PairNotFound");
        }

        return View("Pair", model);
    }

    /// <summary>
    /// The pair page, assembled. Called by the first paint and by every push on the live stream —
    /// one loader, so a figure cannot mean one thing when the page is opened and another thing when
    /// it is updated.
    /// </summary>
    private Task<PairPageModel?> LoadAsync(string baseFamily, CancellationToken ct) =>
        PairPageLoader.LoadAsync(_db, _cache, _clock, baseFamily, ct);

    /// <summary>
    /// The regions of the pair page that a pass can change, each one the partial that drew it the
    /// first time. The candle panels are absent on purpose — see the note on
    /// <see cref="LivePageController.Live"/>.
    /// </summary>
    protected override (string Region, string View)[] LiveRegions { get; } =
    [
        ("statement", "_Statement"),
        ("table", "_PairTable"),
        ("stamps", "_Stamps"),
    ];

    protected override Task<PairPageModel?> LoadPageAsync(string baseFamily, CancellationToken ct) =>
        LoadAsync(baseFamily, ct);

    /// <summary>
    /// What the cache holds for one pair: the comparison and the hourly bars behind it, together,
    /// because they are fetched on one connection for one request and splitting them would double
    /// the round trips to halve nothing.
    ///
    /// It carries no "now" — deliberately, and that is the property the whole freshness model rests
    /// on. See <see cref="StudioCache"/>.
    /// </summary>
    private sealed record PairData(
        AssetComparison Comparison,
        IReadOnlyDictionary<int, CandleSeries> Candles,
        IReadOnlyDictionary<int, MetricHourSeries> Metrics);
}

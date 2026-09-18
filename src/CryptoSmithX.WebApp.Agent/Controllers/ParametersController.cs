using CryptoSmithX.WebApp.Agent.Data;
using CryptoSmithX.WebApp.Agent.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace CryptoSmithX.WebApp.Agent.Controllers;

/// <summary>
/// Trading parameters — where a sign-in lands, and the one screen that writes anything.
///
/// It edits ONE bot instance — the one belonging to the signed-in account: its four runtime
/// overrides and strategy revision, and its futures universe preferences. The instance is derived
/// from the session on every request and is never read from the form, so there is no field to
/// tamper with and no way to address someone else's bot — a request carrying another instance id
/// would simply have nowhere to put it.
/// </summary>
[Authorize]
public sealed class ParametersController : Controller
{
    private readonly BotDb _bot;
    private readonly KrakenFuturesCredentialValidator _krakenValidator;
    private readonly KrakenSpotCredentialValidator _krakenSpotValidator;
    private readonly ILogger<ParametersController> _log;

    public ParametersController(
        BotDb bot,
        KrakenFuturesCredentialValidator krakenValidator,
        KrakenSpotCredentialValidator krakenSpotValidator,
        ILogger<ParametersController> log)
    {
        _bot = bot;
        _krakenValidator = krakenValidator;
        _krakenSpotValidator = krakenSpotValidator;
        _log = log;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            await using var connection = await _bot.OpenAsync(ct);
            var owner = await BotInstanceOwnerStore.FindActiveAsync(
                connection,
                User.Identity?.Name ?? string.Empty,
                ct);
            if (owner is null)
            {
                return View("NoBot");
            }

            var strategy = await StrategyProfileStore.LoadActiveAsync(connection, owner.BotInstanceId, ct);
            if (strategy is null)
            {
                return View("NoStrategyProfile");
            }

            var model = await BuildAsync(connection, owner, strategy, errors: null, justSaved: false, posted: null, ct);
            return model is null ? View("RuntimeLimitsUnavailable") : View(model);
        }
        catch (NpgsqlException e)
        {
            // The bot's database is a different system on a different machine, and the route to it
            // can be gone while everything else here is fine. Say that, rather than showing a form
            // filled from the baseline mirror — which would look exactly like a working page and
            // would accept a save that could not land.
            _log.LogError(e, "the bot database is unreachable");
            return View("BotUnreachable");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // THE ONLY CATCH HERE USED TO BE THE ONE ABOVE, and everything else reached the reader
            // as a blank page: a 500 with no body, on a screen whose entire job is to say what the
            // bot is set to. One profile missing one key did exactly that, for as long as nobody
            // read the container log. A page that cannot be built must still be a page that says so.
            _log.LogError(e, "the parameters page could not be built for {Username}", User.Identity?.Name);
            return View("ProfileUnreadable");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ParametersSaveRequest request, CancellationToken ct)
    {
        try
        {
            await using var conn = await _bot.OpenAsync(ct);
            var owner = await BotInstanceOwnerStore.FindActiveAsync(
                conn,
                User.Identity?.Name ?? string.Empty,
                ct);
            if (owner is null)
            {
                return View("NoBot");
            }

            var strategy = await StrategyProfileStore.LoadActiveAsync(conn, owner.BotInstanceId, ct);
            if (strategy is null)
            {
                return View("NoStrategyProfile");
            }

            var universe = await UniversePreferenceStore.LoadAsync(conn, owner, ct);
            var plan = ParametersSavePlan.Build(request, strategy, universeDecidesMarkets: universe is not null);
            if (plan.Errors.Count > 0)
            {
                var model = await BuildAsync(conn, owner, strategy, plan.Errors, justSaved: false, request, ct);
                return model is null ? View("RuntimeLimitsUnavailable") : View(nameof(Index), model);
            }

            var profile = new TradeProfile(
                request.PositionMarginUsd!.Value,
                request.Leverage!.Value,
                request.MaxOpenPositions!.Value,
                request.MaxOpenPositionsPerGroup!.Value);
            await StrategyProfileStore.SaveAsync(
                conn,
                owner.BotInstanceId,
                strategy,
                new StrategyProfileSave(
                    profile,
                    plan.Parameters,
                    request.ChangeNote,
                    User.Identity?.Name ?? string.Empty,
                    plan.ModeToWrite),
                ct);
        }
        catch (StrategyProfileConflictException e)
        {
            _log.LogInformation(e, "the strategy profile changed before {Username} could save it", User.Identity?.Name);
            TempData["SaveConflict"] = true;
            return RedirectToAction(nameof(Index));
        }
        catch (NpgsqlException e)
        {
            _log.LogError(e, "the bot database refused the write");
            return View("BotUnreachable");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Same reasoning as Index, and it matters more here: a save that fell over silently
            // leaves the reader unable to tell whether it landed.
            _log.LogError(e, "the save could not be completed for {Username}", User.Identity?.Name);
            return View("ProfileUnreadable");
        }

        // Post/redirect/get: the page that follows a write is a fresh read of what was written,
        // so a refresh cannot repeat the write and what is on screen is the table's answer rather
        // than the form's.
        TempData["JustSaved"] = true;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The Universe form: how many futures pairs the bot picks by itself, and which it always adds
    /// or never opens. The row written is the signed-in account's bot, resolved here — the form
    /// carries no bot id — and only an existing row is updated.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUniverse(UniverseSaveRequest request, CancellationToken ct)
    {
        try
        {
            await using var conn = await _bot.OpenAsync(ct);
            var owner = await BotInstanceOwnerStore.FindActiveAsync(
                conn,
                User.Identity?.Name ?? string.Empty,
                ct);
            if (owner is null)
            {
                return View("NoBot");
            }

            var strategy = await StrategyProfileStore.LoadActiveAsync(conn, owner.BotInstanceId, ct);
            if (strategy is null)
            {
                return View("NoStrategyProfile");
            }

            var current = await UniversePreferenceStore.LoadAsync(conn, owner, ct);
            if (current is null)
            {
                // The page offers no form without a row, so there is nothing this post may update.
                TempData["UniverseConflict"] = true;
                return RedirectToAction(nameof(Index), null, "universe");
            }

            var futuresPairs = await UniversePreferenceStore.LoadFuturesPairsAsync(conn, ct);
            var validation = UniversePairList.Validate(
                request.AutoInstrumentCount,
                request.ForceIncludePairs,
                request.ForceExcludePairs,
                futuresPairs);
            var errors = new Dictionary<string, string>(validation.Errors, StringComparer.Ordinal);
            if (request.UniverseVersion != current.UpdatedAt.Ticks)
            {
                errors["universe"] = "Poros pasikeitė, kol buvo atidarytas šis puslapis. Peržiūrėk ir išsaugok dar kartą.";
            }

            if (errors.Count > 0)
            {
                var model = await BuildAsync(conn, owner, strategy, errors: null, justSaved: false, posted: null, ct, request, errors);
                return model is null ? View("RuntimeLimitsUnavailable") : View(nameof(Index), model);
            }

            var saved = await UniversePreferenceStore.SaveAsync(
                conn,
                owner,
                validation.Selection!,
                current.UpdatedAt,
                User.Identity?.Name ?? string.Empty,
                ct);
            TempData[saved ? "UniverseSaved" : "UniverseConflict"] = true;
        }
        catch (NpgsqlException e)
        {
            _log.LogError(e, "the bot database refused the universe write");
            return View("BotUnreachable");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "the universe save could not be completed for {Username}", User.Identity?.Name);
            return View("ProfileUnreadable");
        }

        return RedirectToAction(nameof(Index), null, "universe");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateKrakenCredentials(KrakenCredentialRequest request, CancellationToken ct)
    {
        try
        {
            await using var connection = await _bot.OpenAsync(ct);
            var owner = await BotInstanceOwnerStore.FindActiveAsync(
                connection,
                User.Identity?.Name ?? string.Empty,
                ct);
            if (owner is null)
            {
                return Unauthorized();
            }

            var result = await ValidateKrakenCredentialsAsync(request, ct);
            return Json(new { valid = result.IsValid, message = result.Message, permissions = result.Permissions });
        }
        catch (NpgsqlException e)
        {
            _log.LogError(e, "the bot database is unreachable while validating a Kraken credential");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { valid = false, message = "Boto duomenų bazė nepasiekiama." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateSavedKrakenCredentials(string? scope, CancellationToken ct)
    {
        try
        {
            if (!KrakenCredentialStore.IsSupportedScope(scope))
            {
                return BadRequest(new { valid = false, message = "Nepalaikoma Kraken paskyros rūšis." });
            }

            await using var connection = await _bot.OpenAsync(ct);
            var owner = await BotInstanceOwnerStore.FindActiveAsync(
                connection,
                User.Identity?.Name ?? string.Empty,
                ct);
            if (owner is null)
            {
                return Unauthorized();
            }

            var credentials = await KrakenCredentialStore.LoadAsync(connection, owner.BotInstanceId, scope!, ct);
            if (credentials is null)
            {
                return Json(new { valid = false, message = "Nėra išsaugotų raktų, kuriuos būtų galima patikrinti." });
            }

            var result = await ValidateKrakenCredentialsAsync(new KrakenCredentialRequest
            {
                Scope = scope,
                ApiKey = credentials.ApiKey,
                ApiSecret = credentials.ApiSecret,
            }, ct);
            return Json(new { valid = result.IsValid, message = result.Message, permissions = result.Permissions });
        }
        catch (NpgsqlException e)
        {
            _log.LogError(e, "the bot database is unreachable while validating a saved Kraken credential");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { valid = false, message = "Boto duomenų bazė nepasiekiama." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveKrakenCredentials(KrakenCredentialRequest request, CancellationToken ct)
    {
        try
        {
            await using var connection = await _bot.OpenAsync(ct);
            var owner = await BotInstanceOwnerStore.FindActiveAsync(
                connection,
                User.Identity?.Name ?? string.Empty,
                ct);
            if (owner is null)
            {
                return View("NoBot");
            }

            var validation = await ValidateKrakenCredentialsAsync(request, ct);
            if (!validation.IsValid)
            {
                TempData["KrakenCredentialError"] = validation.Message;
                return RedirectToAction(nameof(Index));
            }

            if (!KrakenCredentialStore.IsSupportedScope(request.Scope))
            {
                TempData["KrakenCredentialError"] = "Nepalaikoma Kraken paskyros rūšis.";
                return RedirectToAction(nameof(Index));
            }

            await KrakenCredentialStore.SaveAsync(
                connection,
                owner.BotInstanceId,
                request.Scope!,
                request.ApiKey!.Trim(),
                request.ApiSecret!.Trim(),
                ct);
            TempData["KrakenCredentialNotice"] = $"{ScopeTitle(request.Scope)} raktas išsaugotas. Botas naudos jį per kitą savo atnaujinimą.";
        }
        catch (NpgsqlException e)
        {
            _log.LogError(e, "the bot database refused a Kraken credential write");
            TempData["KrakenCredentialError"] = "Boto duomenų bazė nepasiekiama.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeKrakenCredentials(string? scope, CancellationToken ct)
    {
        try
        {
            await using var connection = await _bot.OpenAsync(ct);
            var owner = await BotInstanceOwnerStore.FindActiveAsync(
                connection,
                User.Identity?.Name ?? string.Empty,
                ct);
            if (owner is null)
            {
                return View("NoBot");
            }

            if (!KrakenCredentialStore.IsSupportedScope(scope))
            {
                TempData["KrakenCredentialError"] = "Nepalaikoma Kraken paskyros rūšis.";
                return RedirectToAction(nameof(Index));
            }

            await KrakenCredentialStore.RemoveAsync(connection, owner.BotInstanceId, scope!, ct);
            TempData["KrakenCredentialNotice"] = $"{ScopeTitle(scope)} raktų pora pašalinta iš boto duomenų bazės.";
        }
        catch (NpgsqlException e)
        {
            _log.LogError(e, "the bot database refused a Kraken credential removal");
            TempData["KrakenCredentialError"] = "Boto duomenų bazė nepasiekiama.";
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<ParametersViewModel?> BuildAsync(
        NpgsqlConnection connection,
        BotInstanceOwner owner,
        ActiveStrategyProfile strategy,
        IReadOnlyDictionary<string, string>? errors,
        bool justSaved,
        ParametersSaveRequest? posted,
        CancellationToken ct,
        UniverseSaveRequest? postedUniverse = null,
        IReadOnlyDictionary<string, string>? universeErrors = null)
    {
        var overrides = await TradeProfileStore.LoadAsync(connection, owner.BotInstanceId, ct);
        if (overrides.Count != TradeProfileKeys.All.Length)
        {
            return null;
        }

        var lastWritten = overrides.Count > 0
            ? await TradeProfileStore.LastWrittenAsync(connection, owner.BotInstanceId, ct)
            : null;

        var profile = new TradeProfile(
            overrides[TradeProfileKeys.PositionMarginUsd],
            overrides[TradeProfileKeys.Leverage],
            decimal.ToInt32(overrides[TradeProfileKeys.MaxOpenPositions]),
            decimal.ToInt32(overrides[TradeProfileKeys.MaxOpenPositionsPerGroup]));

        // A refused save keeps what the person typed, not what the table holds: handing back the
        // stored value would quietly discard three good edits because a fourth was out of range.
        if (posted is not null)
        {
            profile = profile with
            {
                PositionMarginUsd = posted.PositionMarginUsd ?? profile.PositionMarginUsd,
                Leverage = posted.Leverage ?? profile.Leverage,
                MaxOpenPositions = posted.MaxOpenPositions ?? profile.MaxOpenPositions,
                MaxOpenPositionsPerGroup = posted.MaxOpenPositionsPerGroup ?? profile.MaxOpenPositionsPerGroup,
            };
        }

        var values = StrategyProfileStore.ResolveValues(strategy);
        var runtimeLimits = BuildRuntimeLimits(profile, errors);
        var universe = await UniversePreferenceStore.LoadAsync(connection, owner, ct);
        var futuresPairs = await UniversePreferenceStore.LoadFuturesPairsAsync(connection, ct);

        var exitMode = StrategyParameterViews.ExitMode(values, posted);
        var parameters = StrategyParameterViews.Build(
            values,
            exitMode.AtrMode,
            posted,
            errors,
            universe?.AutoInstrumentCount);
        var history = await StrategyProfileStore.LoadHistoryAsync(connection, strategy.ProfileId, 8, ct);
        // Npgsql permits one active command per connection. These are cheap point reads, so keep
        // them sequential rather than hiding a second connection behind a cosmetic parallelism.
        var futuresCredentials = await KrakenCredentialStore.LoadSummaryAsync(
            connection,
            owner.BotInstanceId,
            KrakenCredentialStore.FuturesScope,
            ct);
        var spotCredentials = await KrakenCredentialStore.LoadSummaryAsync(
            connection,
            owner.BotInstanceId,
            KrakenCredentialStore.SpotScope,
            ct);

        return new ParametersViewModel
        {
            BotInstanceId = owner.BotInstanceId,
            PublicAlias = owner.PublicAlias,
            StrategyProfileName = strategy.ProfileName,
            StrategyRevision = strategy.Revision,
            StrategyProfileId = strategy.ProfileId,
            Profile = profile,
            LastWritten = lastWritten,
            RuntimeLimits = runtimeLimits,
            StrategyParameters = parameters,
            ExitMode = exitMode,
            Universe = BuildUniverse(universe, futuresPairs.Count, postedUniverse, universeErrors),
            RevisionHistory = history,
            Errors = errors ?? new Dictionary<string, string>(StringComparer.Ordinal),
            JustSaved = justSaved || TempData["JustSaved"] is true,
            SaveConflict = TempData["SaveConflict"] is true,
            KrakenCredentials =
            [
                new(KrakenCredentialStore.SpotScope, "Spot trading API", spotCredentials),
                new(KrakenCredentialStore.FuturesScope, "Futures trading API", futuresCredentials),
            ],
            KrakenCredentialNotice = TempData["KrakenCredentialNotice"] as string,
            KrakenCredentialError = TempData["KrakenCredentialError"] as string,
        };
    }

    private Task<KrakenCredentialValidation> ValidateKrakenCredentialsAsync(
        KrakenCredentialRequest request,
        CancellationToken ct) =>
        request.Scope switch
        {
            KrakenCredentialStore.FuturesScope => _krakenValidator.ValidateAsync(request.ApiKey, request.ApiSecret, ct),
            KrakenCredentialStore.SpotScope => _krakenSpotValidator.ValidateAsync(request.ApiKey, request.ApiSecret, ct),
            _ => Task.FromResult(KrakenCredentialValidation.Invalid("Nepalaikoma Kraken paskyros rūšis.")),
        };

    private static string ScopeTitle(string? scope) => scope == KrakenCredentialStore.SpotScope
        ? "Kraken Spot"
        : "Kraken Futures";

    private UniverseViewModel BuildUniverse(
        UniversePreferences? row,
        int registryPairCount,
        UniverseSaveRequest? posted,
        IReadOnlyDictionary<string, string>? errors)
    {
        var include = row is null ? string.Empty : UniversePairList.Format(row.ForceIncludePairs);
        var exclude = row is null ? string.Empty : UniversePairList.Format(row.ForceExcludePairs);

        // A refused save keeps what was typed, as the strategy form does.
        return new UniverseViewModel(
            Configured: row is not null,
            AutoInstrumentCount: posted?.AutoInstrumentCount ?? row?.AutoInstrumentCount ?? 0,
            ForceIncludeText: posted is null ? include : posted.ForceIncludePairs ?? string.Empty,
            ForceExcludeText: posted is null ? exclude : posted.ForceExcludePairs ?? string.Empty,
            SavedAutoInstrumentCount: row?.AutoInstrumentCount ?? 0,
            SavedForceIncludeCount: row?.ForceIncludePairs.Length ?? 0,
            SavedForceExcludeCount: row?.ForceExcludePairs.Length ?? 0,
            RegistryPairCount: registryPairCount,
            Version: row?.UpdatedAt.Ticks ?? 0,
            UpdatedAt: row?.UpdatedAt,
            UpdatedBy: row?.UpdatedBy,
            Errors: errors ?? new Dictionary<string, string>(StringComparer.Ordinal),
            JustSaved: TempData["UniverseSaved"] is true,
            Conflict: TempData["UniverseConflict"] is true);
    }

    private static IReadOnlyList<RuntimeLimitViewModel> BuildRuntimeLimits(
        TradeProfile profile,
        IReadOnlyDictionary<string, string>? errors) =>
    [
        new(
            TradeProfileKeys.PositionMarginUsd,
            "positionMarginUsd",
            "Pozicijos marža",
            "USD",
            profile.PositionMarginUsd,
            0m,
            5_000m,
            0.01m,
            2,
            "Kiek savo pinigų skiri vienai pozicijai.",
            $"Pvz. {profile.PositionMarginUsd:0.##} USD × {profile.Leverage:0.#} svertas = {profile.PositionMarginUsd * profile.Leverage:0.##} USD pozicija.",
            "Mažiau vienam sandoriui",
            "Daugiau vienam sandoriui",
            "Tai tavo pinigų dalis, kurią botas skiria vienam sandoriui. Su svertu ji virsta didesne pozicija rinkoje.",
            "Vienas sandoris rizikuos mažesne suma, bet ir uždirbs mažiau.",
            "Vienas sandoris taps svarbesnis — didesnis ir pelnas, ir galimas nuostolis.",
            "Pozicijos dydis skaičiuojamas kaip marža × svertas / įėjimo kaina, apvalinant pagal biržos kiekio žingsnį.",
            errors?.GetValueOrDefault(TradeProfileKeys.PositionMarginUsd)),
        new(
            TradeProfileKeys.Leverage,
            "leverage",
            "Svertas",
            "×",
            profile.Leverage,
            1m,
            10m,
            0.1m,
            1,
            "Padidina ir galimą pelną, ir nuostolį.",
            $"Pvz. su {profile.PositionMarginUsd:0.##} USD ir {profile.Leverage:0.#}× valdai {profile.PositionMarginUsd * profile.Leverage:0.##} USD poziciją.",
            "Mažiau rizikos",
            "Daugiau rizikos",
            "Svertas padidina poziciją, neprašydamas daugiau tavo pinigų. Kartu jis tiek pat kartų padidina ir nuostolį.",
            "Pozicija mažesnė, kaina turi nueiti toliau, kad rezultatas būtų juntamas.",
            "Pozicija didesnė — greičiau uždirbsi ir greičiau pasieksi stop loss.",
            "Svertas nustatomas biržos pusėje prieš įėjimą. Stop loss atstumas ATR vienetais nesikeičia, o nuostolis pinigais auga proporcingai.",
            errors?.GetValueOrDefault(TradeProfileKeys.Leverage)),
        new(
            TradeProfileKeys.MaxOpenPositions,
            "maxOpenPositions",
            "Daugiausia atvirų pozicijų",
            "poz.",
            profile.MaxOpenPositions,
            1m,
            20m,
            1m,
            0,
            "Kiek sandorių botas gali laikyti vienu metu.",
            $"Pvz. jei jau atidarytos {profile.MaxOpenPositions} pozicijos, naujos neatidarys.",
            "Mažiau pozicijų",
            "Daugiau pozicijų",
            "Riba, kiek sandorių botas gali laikyti tuo pačiu metu.",
            "Mažiau vienu metu veikiančių sandorių — ramesnė, bet lėtesnė prekyba.",
            "Daugiau sandorių vienu metu — didesnė bendra ekspozicija rinkoje.",
            "Tikrinama prieš kiekvieną įėjimą: pasiekus ribą signalas atmetamas ir įrašomas į ciklo žurnalą.",
            errors?.GetValueOrDefault(TradeProfileKeys.MaxOpenPositions)),
        new(
            TradeProfileKeys.MaxOpenPositionsPerGroup,
            "maxOpenPositionsPerGroup",
            "Daugiausia pozicijų vienoje grupėje",
            "poz.",
            profile.MaxOpenPositionsPerGroup,
            1m,
            20m,
            1m,
            0,
            "Neleidžia per daug statyti ant panašiai judančių kriptovaliutų ar akcijų.",
            $"Pvz. jei grupėje jau yra {profile.MaxOpenPositionsPerGroup} pozicijos, kitą botas praleis.",
            "Labiau paskirstyta",
            "Didesnė koncentracija",
            "Panašiai judančios kriptovaliutos ar akcijos suskirstytos į grupes. Riba neleidžia visų pinigų sudėti į tą patį judesį.",
            "Rizika labiau paskirstyta tarp skirtingų kriptovaliutų ar akcijų.",
            "Leidžiama stipriau susitelkti į vieną rinkos temą.",
            "Grupė priskiriama pagal koreliacijos žemėlapį ir tikrinama kartu su bendra atvirų pozicijų riba.",
            errors?.GetValueOrDefault(TradeProfileKeys.MaxOpenPositionsPerGroup)),
    ];

}

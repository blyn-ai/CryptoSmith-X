using CryptoSmithX.WebApp.Agent.Data;
using CryptoSmithX.WebApp.Agent.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace CryptoSmithX.WebApp.Agent.Controllers;

/// <summary>
/// Trading parameters — where a sign-in lands, and the one screen that writes anything.
///
/// It edits the four runtime overrides of ONE bot instance: the one belonging to the signed-in
/// account. The instance is derived from the session on every request and is never read from the
/// form, so there is no field to tamper with and no way to address someone else's bot — a request
/// carrying another instance id would simply have nowhere to put it.
/// </summary>
[Authorize]
public sealed class ParametersController : Controller
{
    private readonly BotDb _bot;
    private readonly ILogger<ParametersController> _log;

    public ParametersController(BotDb bot, ILogger<ParametersController> log)
    {
        _bot = bot;
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
        var posted = new PostedProfile(
            request.PositionMarginUsd,
            request.Leverage,
            request.MaxOpenPositions,
            request.MaxOpenPositionsPerGroup);

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

            var errors = Validate(request, strategy);
            if (request.StrategyProfileId != strategy.ProfileId || request.StrategyRevision != strategy.Revision)
            {
                errors["strategy"] = "Strategija pasikeitė, kol buvo atidarytas šis puslapis. Prieš išsaugant peržiūrėk dabartinę reviziją.";
            }

            if (errors.Count > 0)
            {
                var model = await BuildAsync(conn, owner, strategy, errors, justSaved: false, request, ct);
                return model is null ? View("RuntimeLimitsUnavailable") : View(nameof(Index), model);
            }

            var profile = new TradeProfile(
                request.PositionMarginUsd!.Value,
                request.Leverage!.Value,
                request.MaxOpenPositions!.Value,
                request.MaxOpenPositionsPerGroup!.Value);
            // Resolved ONCE. It used to be rebuilt inside the predicate, so the whole profile was
            // cloned and merged for every parameter in the catalogue.
            var resolved = StrategyProfileStore.ResolveValues(strategy);
            var values = StrategyParameterCatalog.All
                .Where(definition => StrategyParameterCatalog.IsPresent(definition, resolved)
                    && StrategyParameterCatalog.IsEnabled(definition, resolved)
                    && request.Parameters.TryGetValue(definition.Id, out var posted) && posted is not null)
                .ToDictionary(
                    definition => definition.Id,
                    definition => request.Parameters[definition.Id]!.Value,
                    StringComparer.Ordinal);
            await StrategyProfileStore.SaveAsync(
                conn,
                owner.BotInstanceId,
                strategy,
                new StrategyProfileSave(profile, values, request.ChangeNote, User.Identity?.Name ?? string.Empty),
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

    private async Task<ParametersViewModel?> BuildAsync(
        NpgsqlConnection connection,
        BotInstanceOwner owner,
        ActiveStrategyProfile strategy,
        IReadOnlyDictionary<string, string>? errors,
        bool justSaved,
        ParametersSaveRequest? posted,
        CancellationToken ct)
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
        var parameters = StrategyParameterCatalog.All
            .Select(definition => new StrategyParameterViewModel(
                definition,
                posted?.Parameters.GetValueOrDefault(definition.Id) ?? StrategyParameterCatalog.Read(definition, values),
                StrategyParameterCatalog.IsEnabled(definition, values),
                errors?.GetValueOrDefault(definition.Id)))
            .ToList();
        var history = await StrategyProfileStore.LoadHistoryAsync(connection, strategy.ProfileId, 8, ct);

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
            RevisionHistory = history,
            Errors = errors ?? new Dictionary<string, string>(StringComparer.Ordinal),
            JustSaved = justSaved || TempData["JustSaved"] is true,
            SaveConflict = TempData["SaveConflict"] is true,
        };
    }

    /// <summary>
    /// The same four rules the fields carry, checked again on the server. Null means the field was
    /// not a number at all — an empty box, or letters — and is refused rather than read as zero.
    /// </summary>
    private static Dictionary<string, string> Validate(ParametersSaveRequest request, ActiveStrategyProfile strategy)
    {
        var p = new PostedProfile(
            request.PositionMarginUsd,
            request.Leverage,
            request.MaxOpenPositions,
            request.MaxOpenPositionsPerGroup);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        // ZERO IS ALLOWED, and it is the one value here that means something other than a size:
        // no margin is no position to open. It used to be refused as a typo, which left an owner
        // wanting to stop new entries with nothing on this screen to do it with. The bot's own
        // table has no CHECK against it (bot_config_overrides, verified on the test host), so the
        // value stores; what the screen owes the reader is to say loudly what it now means.
        if (p.PositionMarginUsd is not { } margin || margin < 0)
        {
            errors[TradeProfileKeys.PositionMarginUsd] = "Įvesk nulį arba teigiamą skaičių";
        }

        if (p.Leverage is not { } lev || lev < 1 || lev > 10)
        {
            errors[TradeProfileKeys.Leverage] = "Leistina reikšmė nuo 1 iki 10";
        }

        if (p.MaxOpenPositions is not { } max || max < 1 || max > 20)
        {
            errors[TradeProfileKeys.MaxOpenPositions] = "Įvesk sveiką skaičių nuo 1 iki 20";
        }

        if (p.MaxOpenPositionsPerGroup is not { } group || group < 1 || group > 20)
        {
            errors[TradeProfileKeys.MaxOpenPositionsPerGroup] = "Įvesk sveiką skaičių nuo 1 iki 20";
        }
        else if (p.MaxOpenPositions is { } total && group > total)
        {
            // Only when the total itself is valid: two complaints about one mistake is one too many.
            errors[TradeProfileKeys.MaxOpenPositionsPerGroup] = "Negali viršyti bendro pozicijų limito";
        }

        if (request.ChangeNote?.Length > 1_000)
        {
            errors["changeNote"] = "Strategijos pastaba negali būti ilgesnė nei 1000 simbolių";
        }

        var values = StrategyProfileStore.ResolveValues(strategy);
        foreach (var definition in StrategyParameterCatalog.All)
        {
            // A key this profile does not carry has no input on the page, so it is not missing from
            // the post — it was never askable. Complaining "enter a number" about a field nobody was
            // shown is the form blaming the reader for the profile's shape.
            if (!StrategyParameterCatalog.IsPresent(definition, values)
                || !StrategyParameterCatalog.IsEnabled(definition, values))
            {
                continue;
            }

            if (!request.Parameters.TryGetValue(definition.Id, out var value) || value is null)
            {
                errors[definition.Id] = "Įvesk skaičių";
                continue;
            }

            try
            {
                StrategyParameterCatalog.Write(definition, values, value.Value);
            }
            catch (ArgumentException)
            {
                errors[definition.Id] = "Reikšmė nepatenka į leidžiamą ribą";
            }
        }

        return errors;
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

    private sealed record PostedProfile(
        decimal? PositionMarginUsd, decimal? Leverage, int? MaxOpenPositions, int? MaxOpenPositionsPerGroup);
}

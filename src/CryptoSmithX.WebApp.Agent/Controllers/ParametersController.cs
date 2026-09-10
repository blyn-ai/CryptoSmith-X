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
                errors["strategy"] = "The strategy changed while this page was open. Review the current revision before saving.";
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
            var values = StrategyParameterCatalog.All
                .Where(definition => StrategyParameterCatalog.IsEnabled(
                    definition,
                    StrategyProfileStore.ResolveValues(strategy)))
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

        if (p.PositionMarginUsd is not { } margin || margin <= 0)
        {
            errors[TradeProfileKeys.PositionMarginUsd] = "Must be a number greater than zero";
        }

        if (p.Leverage is not { } lev || lev < 1 || lev > 10)
        {
            errors[TradeProfileKeys.Leverage] = "Must be between 1 and 10";
        }

        if (p.MaxOpenPositions is not { } max || max < 1 || max > 20)
        {
            errors[TradeProfileKeys.MaxOpenPositions] = "Must be a whole number between 1 and 20";
        }

        if (p.MaxOpenPositionsPerGroup is not { } group || group < 1 || group > 20)
        {
            errors[TradeProfileKeys.MaxOpenPositionsPerGroup] = "Must be a whole number between 1 and 20";
        }
        else if (p.MaxOpenPositions is { } total && group > total)
        {
            // Only when the total itself is valid: two complaints about one mistake is one too many.
            errors[TradeProfileKeys.MaxOpenPositionsPerGroup] = "Cannot exceed the total maximum";
        }

        if (request.ChangeNote?.Length > 1_000)
        {
            errors["changeNote"] = "A strategy note cannot exceed 1000 characters";
        }

        var values = StrategyProfileStore.ResolveValues(strategy);
        foreach (var definition in StrategyParameterCatalog.All)
        {
            if (!StrategyParameterCatalog.IsEnabled(definition, values))
            {
                continue;
            }

            if (!request.Parameters.TryGetValue(definition.Id, out var value) || value is null)
            {
                errors[definition.Id] = "Must be a number";
                continue;
            }

            try
            {
                StrategyParameterCatalog.Write(definition, values, value.Value);
            }
            catch (ArgumentException e)
            {
                errors[definition.Id] = e.Message;
            }
        }

        return errors;
    }

    private sealed record PostedProfile(
        decimal? PositionMarginUsd, decimal? Leverage, int? MaxOpenPositions, int? MaxOpenPositionsPerGroup);
}

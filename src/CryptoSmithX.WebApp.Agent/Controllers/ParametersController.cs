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
    public async Task<IActionResult> Save(
        decimal? positionMarginUsd,
        decimal? leverage,
        int? maxOpenPositions,
        int? maxOpenPositionsPerGroup,
        CancellationToken ct)
    {
        var posted = new PostedProfile(
            positionMarginUsd, leverage, maxOpenPositions, maxOpenPositionsPerGroup);

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

            var errors = Validate(posted);
            if (errors.Count > 0)
            {
                var model = await BuildAsync(conn, owner, strategy, errors, justSaved: false, posted, ct);
                return model is null ? View("RuntimeLimitsUnavailable") : View(nameof(Index), model);
            }

            var profile = new TradeProfile(
                positionMarginUsd!.Value, leverage!.Value, maxOpenPositions!.Value, maxOpenPositionsPerGroup!.Value);
            await TradeProfileStore.SaveAsync(conn, owner.BotInstanceId, profile, ct);
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
        PostedProfile? posted,
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

        return new ParametersViewModel
        {
            BotInstanceId = owner.BotInstanceId,
            PublicAlias = owner.PublicAlias,
            StrategyProfileName = strategy.ProfileName,
            StrategyRevision = strategy.Revision,
            Profile = profile,
            LastWritten = lastWritten,
            Errors = errors ?? new Dictionary<string, string>(StringComparer.Ordinal),
            JustSaved = justSaved || TempData["JustSaved"] is true,
        };
    }

    /// <summary>
    /// The same four rules the fields carry, checked again on the server. Null means the field was
    /// not a number at all — an empty box, or letters — and is refused rather than read as zero.
    /// </summary>
    private static Dictionary<string, string> Validate(PostedProfile p)
    {
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

        return errors;
    }

    private sealed record PostedProfile(
        decimal? PositionMarginUsd, decimal? Leverage, int? MaxOpenPositions, int? MaxOpenPositionsPerGroup);
}

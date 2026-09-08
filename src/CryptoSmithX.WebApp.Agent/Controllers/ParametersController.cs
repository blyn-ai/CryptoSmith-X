using CryptoSmithX.WebApp.Agent.Data;
using CryptoSmithX.WebApp.Agent.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
    private readonly TradingBotOptions _options;
    private readonly ILogger<ParametersController> _log;

    public ParametersController(BotDb bot, IOptions<TradingBotOptions> options, ILogger<ParametersController> log)
    {
        _bot = bot;
        _options = options.Value;
        _log = log;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var instance = _options.InstanceFor(User.Identity?.Name);
        if (instance is null)
        {
            // Signed in, but this account owns no bot. Not a 404 — the page exists — and not a
            // blank form, which would invite someone to fill in numbers that could never be saved.
            return View("NoBot");
        }

        try
        {
            return View(await BuildAsync(instance, errors: null, justSaved: false, posted: null, ct));
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
        var instance = _options.InstanceFor(User.Identity?.Name);
        if (instance is null)
        {
            return View("NoBot");
        }

        var posted = new PostedProfile(
            positionMarginUsd, leverage, maxOpenPositions, maxOpenPositionsPerGroup);
        var errors = Validate(posted);
        if (errors.Count > 0)
        {
            // The browser's own min/max/step stops most of this before it is sent; the same rules
            // are checked again here because the browser is not the last line — anything can post
            // to this action.
            return View(nameof(Index), await BuildAsync(instance, errors, justSaved: false, posted, ct));
        }

        var profile = new TradeProfile(
            positionMarginUsd!.Value, leverage!.Value, maxOpenPositions!.Value, maxOpenPositionsPerGroup!.Value);

        try
        {
            await using var conn = await _bot.OpenAsync(ct);
            await TradeProfileStore.SaveAsync(conn, instance, profile, ct);
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

    private async Task<ParametersViewModel> BuildAsync(
        string instance,
        IReadOnlyDictionary<string, string>? errors,
        bool justSaved,
        PostedProfile? posted,
        CancellationToken ct)
    {
        await using var conn = await _bot.OpenAsync(ct);
        var overrides = await TradeProfileStore.LoadAsync(conn, instance, ct);
        var lastWritten = overrides.Count > 0
            ? await TradeProfileStore.LastWrittenAsync(conn, instance, ct)
            : null;

        var baseline = _options.BaselineFor(instance);

        // An override wins; otherwise the deployed baseline. A key that has neither is shown as
        // zero and said to be unknown — the view marks it — because inventing a number here would
        // be indistinguishable on screen from a real one.
        decimal Value(string key, decimal fromBaseline) =>
            overrides.TryGetValue(key, out var v) ? v : fromBaseline;

        var profile = new TradeProfile(
            Value(TradeProfileKeys.PositionMarginUsd, baseline?.PositionMarginUsd ?? 0m),
            Value(TradeProfileKeys.Leverage, baseline?.Leverage ?? 0m),
            (int)Value(TradeProfileKeys.MaxOpenPositions, baseline?.MaxOpenPositions ?? 0),
            (int)Value(TradeProfileKeys.MaxOpenPositionsPerGroup, baseline?.MaxOpenPositionsPerGroup ?? 0));

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

        var source = overrides.Count switch
        {
            0 => TradeProfileSource.Appsettings,
            var n when n == TradeProfileKeys.All.Length => TradeProfileSource.Overrides,
            _ => TradeProfileSource.Partial,
        };

        return new ParametersViewModel
        {
            BotInstanceId = instance,
            Profile = profile,
            Overridden = overrides.Keys.ToHashSet(StringComparer.Ordinal),
            Source = source,
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

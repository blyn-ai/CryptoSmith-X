using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>What one cell of band 5's grid is allowed to say. Four states, and they are four
/// different facts — collapsing any two of them is the failure this band exists to avoid.</summary>
public enum CoverageState
{
    /// <summary>A share of the window is held, and the cell prints it.</summary>
    Held,

    /// <summary>The dataset is collected here and coverage says we hold none of the window. A
    /// measurement, and a bad one.</summary>
    Empty,

    /// <summary>This venue is not set to collect this dataset — it does not publish it, or we have
    /// decided against it. NOT our gap, and it must not read as one.</summary>
    NotCollected,

    /// <summary>Collected, but coverage keeps no rows for it: the feed datasets write as the venue
    /// speaks rather than over a requested range, so there is no window to hold a share of. Their
    /// freshness is band 1's answer, not this grid's.</summary>
    NotTracked,

    /// <summary>The pair was never decided — no row in the matrix at all.</summary>
    Unknown,
}

public sealed record CoverageCellView(CoverageState State, int Percent, string Title)
{
    /// <summary>A share of the window, or a dash. Nothing else: the grid is one metric on one
    /// scale, and a word in a cell of it is a second metric wearing the same shape.</summary>
    public string Text => State == CoverageState.Held || State == CoverageState.Empty
        ? Percent.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    public string Class => State == CoverageState.Held || State == CoverageState.Empty
        ? ""
        : "v2-cov-cell--none";
}

/// <summary>
/// The datasets band 5 accounts for, and why each one is on the page.
///
/// EVERY DATASET THE PAGE DRAWS FROM IS LISTED, whether or not anything was collected. The grid used
/// to be built from the coverage rows themselves, so a dataset nobody had collected disappeared from
/// the band — which is to say the band called "what is behind the figures" went quiet in exactly the
/// case a reader needs it: the figure that is not there.
///
/// <c>Tracked</c> says whether coverage is the right measure for it. The history datasets are asked
/// for a RANGE and can report what share of it they hold; the feed datasets are written as the venue
/// speaks, so there is no requested window to hold a share of, and printing 0 % against them would
/// invent a shortfall. Band 1 answers for those, in ages.
/// </summary>
public sealed record V2Dataset(string Code, string Label, bool Tracked, string Draws);

public static class V2Coverage
{
    public static IReadOnlyList<V2Dataset> Datasets { get; } =
    [
        new("snapshot", "Snapshot", false, "band 1 — price, spread, funding rate, open interest"),
        new("candles", "Candles 1m", true, "band 3 — the price cut"),
        new("funding", "Funding", true, "band 3 — the funding cut"),
        new("open_interest", "Open interest", true, "band 3 — the open-interest cut"),
        new("trades", "Trades", false, "band 4 — the tape"),
        new("book", "Level book", false, "band 2 — the level book"),
        new("liquidations", "Liquidations", true, "band 3 — the liquidations cut"),
    ];

    public static CoverageCellView Cell(
        PairPageModel model, V2Dataset dataset, PairVenueRow row)
    {
        var mode = model.Modes.TryGetValue((row.SegmentCode, dataset.Code), out var m) ? m : null;

        if (mode is null)
        {
            return new CoverageCellView(CoverageState.Unknown, 0,
                $"{Names.Dataset(dataset.Code)}: nothing has been decided for this venue");
        }

        if (mode != "collect")
        {
            return new CoverageCellView(CoverageState.NotCollected, 0,
                $"{Names.Dataset(dataset.Code)}: not collected on this venue — this is not a gap in our data, "
                + "it is a dataset we do not take from here");
        }

        if (!dataset.Tracked)
        {
            return new CoverageCellView(CoverageState.NotTracked, 0,
                $"{Names.Dataset(dataset.Code)}: collected as the venue speaks, not over a requested window — "
                + "there is no share of a window to hold. Its freshness is the age in band 1.");
        }

        var held = model.Coverage.FirstOrDefault(
            c => c.Dataset == dataset.Code && c.InstrumentId == row.InstrumentId);

        if (held is null)
        {
            return new CoverageCellView(CoverageState.Unknown, 0,
                $"{Names.Dataset(dataset.Code)}: collected here, and nothing was asked for in the last 24 h");
        }

        return held.PercentHeld == 0
            ? new CoverageCellView(CoverageState.Empty, 0,
                $"{Names.Dataset(dataset.Code)}: asked for, and none of the last 24 h is held")
            : new CoverageCellView(CoverageState.Held, held.PercentHeld,
                $"{Names.Dataset(dataset.Code)}: {held.PercentHeld}% of the last 24 h held");
    }
}

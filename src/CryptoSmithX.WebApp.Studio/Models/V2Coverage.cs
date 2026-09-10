using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// UX audit P0-3: what one cell of band 5's grid is allowed to say — six states, each its own mark,
/// none of them relying on colour alone (a cell also carries a distinct shape: a notch, a dotted
/// border, a hatch, or plain paper).
/// </summary>
public enum CoverageMatrixState
{
    /// <summary>A windowed dataset, a share of the window held, no gap in it.</summary>
    Held,

    /// <summary>A windowed dataset with at least one gap (open or closed) inside the window —
    /// still shows the share held, plus how much of the window a gap covered.</summary>
    Gap,

    /// <summary>A Gap whose most recent cause was rate limiting — the one cause a reader can read
    /// as "the venue said slow down" rather than "something broke".</summary>
    Throttled,

    /// <summary>A live-only dataset (snapshot, book, trades — no requested window to hold a share
    /// of) whose newest record is older than the call's own window.</summary>
    Stale,

    /// <summary>A live-only dataset, inside its window.</summary>
    Live,

    /// <summary>This venue never provides this dataset, or nothing has been decided for it.</summary>
    Unsupported,
}

/// <summary>One cell, fully formed: which state, what it prints, and the hover title — the view
/// draws from this and never re-derives the state.</summary>
/// <param name="Percent">The green-scale width, where one applies (Held/Gap/Throttled) — kept
/// separate from <see cref="Text"/> because Text carries the "· gap 4h" suffix and re-parsing a
/// number back out of a string the view itself would have to keep in sync is the wrong direction
/// for that dependency to run.</param>
public sealed record CoverageCellView(CoverageMatrixState State, string Text, string Title, int? Percent = null)
{
    public string Class => State switch
    {
        CoverageMatrixState.Held => "v2-cov-cell--held",
        CoverageMatrixState.Gap => "v2-cov-cell--gap",
        CoverageMatrixState.Throttled => "v2-cov-cell--gap v2-cov-cell--throttled",
        CoverageMatrixState.Stale => "v2-cov-cell--stale",
        CoverageMatrixState.Live => "v2-cov-cell--live",
        _ => "v2-cov-cell--unsupported",
    };
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
/// invent a shortfall. Band 1 answers for those, in ages — P0-3 gives the matrix the same two ages
/// directly (LIVE / STALE), rather than sending the reader to band 1 to find out.
/// </summary>
public sealed record V2Dataset(string Code, string Label, bool Tracked, string Draws)
{
    /// <summary>The <c>collector_gap.collector</c> value this dataset's gaps are logged under —
    /// almost always its own code, except "book" (the level-book collector is named "depth" in the
    /// schema, from before "book" existed as its own dataset).</summary>
    public string GapCollector => Code == "book" ? "depth" : Code;
}

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

    /// <summary>Past this many seconds of TOTAL gap time in the window, a windowed dataset's gap
    /// is worth a notch even at a high percentage held — a single short blip does not need one,
    /// rule 3's dash-for-zero argument applied to a shape rather than a number: a notch on every
    /// 99% cell would stop meaning anything.</summary>
    private const double NotchFloorSeconds = 60;

    public static CoverageCellView Cell(PairPageModel model, V2Dataset dataset, VenueRowModel r)
    {
        var row = r.Row;
        var mode = model.Modes.TryGetValue((row.SegmentCode, dataset.Code), out var m) ? m : null;

        if (mode is null || mode != "collect")
        {
            return new CoverageCellView(CoverageMatrixState.Unsupported, "n/a",
                $"{Names.Dataset(dataset.Code)}: not collected on this venue — this is not a gap in "
                + "our data, it is a dataset we do not take from here, or nothing has been decided for it");
        }

        return dataset.Tracked ? TrackedCell(model, dataset, r) : LiveCell(model, dataset, r);
    }

    /// <summary>HELD / GAP / THROTTLED — a dataset asked for over a window, so a share of it can
    /// be held and a gap can sit inside it.</summary>
    private static CoverageCellView TrackedCell(PairPageModel model, V2Dataset dataset, VenueRowModel r)
    {
        var row = r.Row;

        // P0-2 point 5: candles and funding read the SAME window band 3 itself renders — the
        // series already loaded for the sparklines/candle panels — rather than the `coverage`
        // table's own aggregate, which measures REQUEST coverage over the raw history and can
        // read 100% while the rollup has materialised only a few of the 25 hourly bars band 3
        // actually draws. Open interest and liquidations have no such second source on this page
        // and keep reading the `coverage` aggregate.
        int percent;
        if (dataset.Code == "candles")
        {
            var series = V2Band3.Of(model, r);
            percent = series.Windows.Count > 0 ? series.Present * 100 / series.Windows.Count : 0;
        }
        else if (dataset.Code == "funding")
        {
            var windows = r.Metrics.Windows;
            var held = r.Metrics.Funding.Count(v => v is not null);
            percent = windows.Count > 0 ? held * 100 / windows.Count : 0;
        }
        else
        {
            var coverage = model.Coverage.FirstOrDefault(
                c => c.Dataset == dataset.Code && c.InstrumentId == row.InstrumentId);
            percent = coverage?.PercentHeld ?? 0;
        }

        // Gaps are logged per SEGMENT, not per instrument (collector_gap carries no instrument
        // id) — every listing on this segment shares the same gap state for this dataset, which
        // is the same granularity the pass that WROTE the gap actually failed at.
        var gaps = model.Gaps
            .Where(g => g.SegmentCode == row.SegmentCode && g.Collector == dataset.GapCollector)
            .ToList();

        var now = model.RenderedAt.UtcDateTime;
        var totalGapSeconds = gaps.Sum(g => ((g.GapEnd ?? now) - g.GapStart).TotalSeconds);

        if (totalGapSeconds < NotchFloorSeconds)
        {
            return percent > 0
                ? new CoverageCellView(CoverageMatrixState.Held, percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"{Names.Dataset(dataset.Code)}: {percent}% of the last 24 h held", Percent: percent)
                : new CoverageCellView(CoverageMatrixState.Gap, "0",
                    $"{Names.Dataset(dataset.Code)}: none of the last 24 h is held" + (gaps.Count == 0
                        ? ", and no gap is logged for it — asked for and came back empty, or never asked at all"
                        : ""), Percent: 0);
        }

        var mostRecent = gaps[0]; // GapsAsync orders open-first, then newest gap_start first.
        var throttled = mostRecent.Cause == "rate_limited";
        var gapText = Format.ShortAge(totalGapSeconds);
        var text = $"{percent} · gap {gapText}";
        var title = $"{Names.Dataset(dataset.Code)}: {percent}% of the last 24 h held, "
            + $"{gapText} lost to {(throttled ? "throttling" : "a gap")} ({GapVoice.Say(mostRecent.Cause)})";

        return new CoverageCellView(
            throttled ? CoverageMatrixState.Throttled : CoverageMatrixState.Gap, text, title, Percent: percent);
    }

    /// <summary>LIVE / STALE — a dataset with no requested window, judged against the call's own
    /// freshness window the way band 1's ages already are.</summary>
    private static CoverageCellView LiveCell(PairPageModel model, V2Dataset dataset, VenueRowModel r)
    {
        var (ageSeconds, windowSeconds) = dataset.Code switch
        {
            "snapshot" => (r.Ages.PriceSeconds, r.Windows.PriceSeconds),
            "book" => (r.Ages.DepthSeconds, r.Windows.DepthSeconds),
            // Trades has no call/window pair of its own on this page (V2Store.TapeAsync reads the
            // last hour, unbounded by a stated cadence) — "on the tape" IS the source P0-3's own
            // example names, so the newest fill for this instrument, wherever it sits in the
            // shared 18-row tape, is what this reads.
            "trades" => (LastTapeAgeSeconds(model, r.Row.InstrumentId), (double?)3600),
            _ => ((double?)null, (double?)null),
        };

        if (ageSeconds is null)
        {
            return new CoverageCellView(CoverageMatrixState.Stale, "stale —",
                $"{Names.Dataset(dataset.Code)}: collected here, and nothing has been observed yet");
        }

        var age = Format.ShortAge(ageSeconds);
        return windowSeconds is { } w && ageSeconds > w
            ? new CoverageCellView(CoverageMatrixState.Stale, "stale " + age,
                $"{Names.Dataset(dataset.Code)}: newest record is {age} old, past its own window")
            : new CoverageCellView(CoverageMatrixState.Live, "live · " + age,
                $"{Names.Dataset(dataset.Code)}: newest record {age} old, inside its own window");
    }

    private static double? LastTapeAgeSeconds(PairPageModel model, int instrumentId)
    {
        var latest = model.Tape.Where(t => t.InstrumentId == instrumentId)
            .Select(t => (DateTime?)t.EventTime).DefaultIfEmpty(null).Max();
        return latest is { } t ? (model.RenderedAt.UtcDateTime - t).TotalSeconds : null;
    }
}

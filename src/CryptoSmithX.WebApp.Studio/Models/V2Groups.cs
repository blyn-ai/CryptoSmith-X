using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// The seven questions the v2 table is organised by, and the fields each one opens to.
///
/// GROUPED BY THE QUESTION A READER ASKS, not by the call that wrote the figure. The first design
/// banded the columns as TICKER CALL / OI CALL / DEPTH SWEEP, which is honest about our plumbing and
/// useless to the person reading: they are comparing venues, not our HTTP requests. Which call wrote
/// a figure is still on the page — it is the age in the listing cell, where it belongs, because an
/// age is a fact about a call and not about a cell.
///
/// The point of the grouping is that the table stops growing sideways. A new metric lands INSIDE a
/// group, behind the same header, instead of adding a column; with sixteen venues and eight datasets
/// a column-per-figure table is unreadable long before the data is complete.
/// </summary>
public static class V2Groups
{
    /// <summary>
    /// The hourly history behind a group's HEADLINE figure — the sparkline the collapsed cell
    /// draws — or an empty list where none is kept.
    ///
    /// The collapsed table is the only place on the page that shows direction across every venue at
    /// once: band 3 answers "how did this one field get here" and can only draw one field at a
    /// time. Two of the seven keep no history at all (a rolling 24-hour turnover is one number, and
    /// an age is measured against now), and those keep the log bar alone — rule 11.
    /// </summary>
    public static IReadOnlyList<double?> Spark(VenueRowModel r, V2Group g) => g.Key switch
    {
        "price" => r.Metrics.Spread,
        "liquidity" => r.Metrics.Depth25,
        "positions" => r.Metrics.OpenInterest,
        "carry" => r.Metrics.Funding,
        "stress" => r.Liquidations,
        _ => [],
    };

    public static IReadOnlyList<V2Group> All { get; } =
    [
        new("price", "Price", "quote asset · bid / ask · bps", PairColumn.SpreadBps, CallTone.Ticker,
            [new("Bid", V2Field.Bid), new("Ask", V2Field.Ask),
             new("Last", V2Field.Last), new("Mark", V2Field.Mark), new("Index", V2Field.Index),
             new("Spread", V2Field.Spread), new("Venue clock", V2Field.VenueClock),
             new("Last trade", V2Field.LastTrade)])
            { Cut = "price" },

        new("liquidity", "Liquidity", "base units · bid / ask at 25 bps", PairColumn.Depth25, CallTone.Depth,
            [new("Bid size", V2Field.BidSize), new("Ask size", V2Field.AskSize),
             new("10 bps", V2Field.Depth10), new("25 bps", V2Field.Depth25),
             new("50 bps", V2Field.Depth50), new("Book reach", V2Field.BookReach)])
            { Cut = "depth25" },

        new("positions", "Positions", "base units · × multiplier applied", PairColumn.OpenInterest, CallTone.OpenInterest,
            [new("OI base", V2Field.OpenInterest), new("Multiplier", V2Field.Multiplier)])
            { Cut = "oi" },

        new("carry", "Cost of carry", "per cent per day · venue interval normalised", null, CallTone.Ticker,
            [new("Per day", V2Field.FundingPerDay), new("Venue rate", V2Field.FundingRate),
             new("Interval", V2Field.FundingInterval)])
            { Cut = "funding" },

        new("activity", "Activity", "quote asset · rolling 24 h", PairColumn.Turnover24h, CallTone.Ticker,
            [new("Turnover Q", V2Field.Turnover24h)])
            { Cut = "turnover" },

        // Единица переехала к числу (правило 9), а объём и был единственной цифрой: группа из
        // одного поля, повторяющего заголовок, — это не группа.
        new("stress", "Stress", "liquidations, last hour", null, CallTone.Ticker, [])
            { Cut = "liquidations" },

        // ПОЛЕЙ НЕТ. Здесь лежали те же три возраста, что уже нарисованы полоской свежести в
        // клетке листинга, — группа целиком дублировала соседнюю клетку. Осталось то, чего
        // полоска сказать не может: насколько худший вызов прошёл СВОЁ окно, и какой он.
        new("trust", "Trust", "the worst call against its own window", null, CallTone.Ticker, []),
    ];
}

/// <summary>One column of the v2 table: a headline figure, a unit, and the fields it opens to.</summary>
/// <param name="Headline">
/// The column the bar is drawn from, or null when the group has no single comparable figure —
/// carry is normalised per venue interval and trust is an age, and neither ranks against a maximum.
/// </param>
public sealed record V2Group(
    string Key,
    string Name,
    string Unit,
    PairColumn? Headline,
    CallTone Tone,
    IReadOnlyList<V2FieldRef> Fields)
{
    /// <summary>
    /// The cut bands 2 and 3 switch to when this group's caret is clicked, or null when the group
    /// has no second dimension to show.
    ///
    /// Six of the seven groups have one. TRUST does not, and that is not an oversight: the ages it
    /// prints are measured against the instant of the request, so an hourly series of them would be
    /// a series of a subtraction we did not make at those hours.
    /// </summary>
    public string? Cut { get; init; }
}

public sealed record V2FieldRef(string Label, V2Field Field);

/// <summary>Every figure the expanded groups can show. Not every venue answers every one.</summary>
/// <summary>
/// Which ranked column a figure IS, or null when it is not one.
///
/// Ranks are computed per FIGURE and never per group: one venue can hold the best bid and the worst
/// ask at the same instant, and a rank stated once for the group would hide exactly that. Fields
/// with no column here are not unranked by oversight — a venue clock, a contract multiplier and a
/// funding interval are facts about the venue, not positions in a comparison.
/// </summary>
public static class V2Ranks
{
    public static PairColumn? ColumnOf(V2Field f) => f switch
    {
        V2Field.Bid => PairColumn.Bid,
        V2Field.Ask => PairColumn.Ask,
        V2Field.Spread => PairColumn.SpreadBps,
        V2Field.BidSize => PairColumn.BidSize,
        V2Field.AskSize => PairColumn.AskSize,
        V2Field.Depth10 => PairColumn.Depth10,
        V2Field.Depth25 => PairColumn.Depth25,
        V2Field.Depth50 => PairColumn.Depth50,
        V2Field.OpenInterest => PairColumn.OpenInterest,
        V2Field.Turnover24h => PairColumn.Turnover24h,
        _ => null,
    };

    /// <summary>The word the spread column uses for the same two ranks. Every other column says
    /// BEST and WORST; on a spread "best" is a width, and the reader reads TIGHT faster than they
    /// translate.</summary>
    public static string Word(V2Field f, Verdict v) => (f, v) switch
    {
        (V2Field.Spread, Verdict.Best) => "tight",
        (V2Field.Spread, Verdict.Worst) => "wide",
        (_, Verdict.Best) => "best",
        _ => "worst",
    };
}

public enum V2Field
{
    Bid, Ask, Last, Mark, Index, Spread, VenueClock, LastTrade,
    BidSize, AskSize, Depth10, Depth25, Depth50, BookReach,
    OpenInterest, Multiplier,
    FundingPerDay, FundingRate, FundingInterval,
    Turnover24h,
    LiquidationVolume, LiquidationUnit,
    AgePrice, AgeDepth, AgeOpenInterest,
}

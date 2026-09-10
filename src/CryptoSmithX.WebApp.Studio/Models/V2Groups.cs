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

    /// <summary>
    /// P0-1: whether this field's chip was ranked inside the listing's own quote asset — the
    /// population the audit finding calls "inside quote group" — rather than across every listing
    /// on the page regardless of currency. Read from <see cref="Verdicts.Scope"/>, the one place
    /// that decides it, so a column moved between scopes there changes both the ranking and this
    /// flag in the same edit.
    /// </summary>
    public static bool IsQuoteScoped(V2Field f) =>
        ColumnOf(f) is { } column && Verdicts.Scope(column) == VerdictScope.PerQuoteAsset;

    /// <summary>
    /// Prompt 1.5: whether this rank names the row holding the HIGHEST figure in its ranking
    /// population (MAX) rather than the LOWEST (MIN).
    ///
    /// Not the same question as "is this Verdict.Best" — Best names the GOOD figure, and the good
    /// figure is the high one only where <see cref="Verdicts.HighIsBest"/> says so. On Ask and on
    /// Spread the good price is the low one, so Verdict.Best there is the row holding the MINIMUM,
    /// and a mark that only ever asked "is this Best" would have printed MAX on the lowest ask on
    /// the page.
    /// </summary>
    public static bool IsMax(V2Field f, Verdict v)
    {
        var highIsBest = ColumnOf(f) is { } c && Verdicts.HighIsBest(c);
        return (v == Verdict.Best) == highIsBest;
    }

    /// <summary>
    /// The class suffix a rank chip wears — <c>.v2-part--best</c> / <c>.v2-part--worst</c>,
    /// unchanged names so Prompt 1.5 needed no selector rewrite in studio-v2.css. What changed is
    /// what decides between them: now <see cref="IsMax"/>, not <see cref="Verdict"/> directly, so
    /// the class some MAX chip wears is always "best" and some MIN chip's is always "worst" even on
    /// Ask and Spread, where the two used to point the other way (a "worst" class rendering the
    /// word "best" for the row holding the MINIMUM ask has always been a contradiction the class
    /// name alone could not see).
    /// </summary>
    public static string ClassWord(V2Field f, Verdict v) => IsMax(f, v) ? "best" : "worst";

    /// <summary>
    /// Prompt 1.5: the mark's own text — always MAX or MIN, replacing BEST/WORST/TIGHT/WIDE
    /// everywhere on this page. States which figure in the ranking population this one is, never
    /// which one is "good": green and magenta are reserved for bid/ask and sign, not for a rank
    /// mark, so the same two words now cover every ranked column including spread, where the
    /// tightest figure is the MINIMUM and the widest is the MAXIMUM whichever one a trader wants.
    /// Lowercase — studio-v2.css's existing text-transform:uppercase on <c>.v2-part i</c> renders
    /// it, the same way BEST/WORST always did.
    /// </summary>
    public static string Mark(V2Field f, Verdict v) => IsMax(f, v) ? "max" : "min";

    /// <summary>
    /// Whether the marked figure is the GOOD end of its column — the second thing a mark says, and
    /// the one the word itself cannot.
    ///
    /// MAX and MIN state which extreme a figure is; they are deliberately silent about whether
    /// that extreme is worth having, because it depends on the column: the biggest depth is good
    /// and the biggest spread is not. That answer already exists — it is <see cref="Verdict"/>,
    /// which Verdicts computes from each column's own HighIsBest — so the colour reads it directly
    /// rather than re-deriving it from the word.
    ///
    /// The two are therefore independent, and both combinations that look odd are correct: MIN in
    /// green on the spread column, MAX in magenta on it. That is the point — a reader learns which
    /// end is good per column without a legend.
    /// </summary>
    public static string ToneWord(Verdict v) => v == Verdict.Best ? "good" : "bad";

    /// <summary>The field name a mark's title states the fact about — "Highest bid among USD
    /// listings", never a column label built for a header (e.g. the paired "Bid / Ask"), because a
    /// paired cell's two chips each name their OWN field.</summary>
    public static string FieldName(V2Field f) => f switch
    {
        V2Field.Bid => "bid",
        V2Field.Ask => "ask",
        V2Field.Spread => "spread",
        V2Field.BidSize => "bid size",
        V2Field.AskSize => "ask size",
        V2Field.Depth10 => "depth at 10 bps",
        V2Field.Depth25 => "depth at 25 bps",
        V2Field.Depth50 => "depth at 50 bps",
        V2Field.OpenInterest => "open interest",
        V2Field.Turnover24h => "turnover",
        _ => f.ToString().ToLowerInvariant(),
    };

    /// <summary>Prompt 1.5, item 3: the mark's title, stating the fact and the population —
    /// "Highest bid among USD listings", "Lowest spread among all listings". <paramref
    /// name="quoteScoped"/> decides the population's own words, read from the same
    /// <see cref="IsQuoteScoped"/>/quote-asset pair the chip's 3px rule already uses, so the title
    /// and the rule can never name two different populations for one chip.</summary>
    public static string Title(V2Field f, Verdict v, bool quoteScoped, string quoteAsset)
    {
        var fact = IsMax(f, v) ? "Highest" : "Lowest";
        var population = quoteScoped ? quoteAsset + " listings" : "all listings";
        return $"{fact} {FieldName(f)} among {population}";
    }
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

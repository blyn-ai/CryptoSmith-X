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
    public static IReadOnlyList<V2Group> All { get; } =
    [
        new("price", "Price", "quote asset · bid / ask · bps", PairColumn.SpreadBps, CallTone.Ticker,
            [new("Last", V2Field.Last), new("Mark", V2Field.Mark), new("Index", V2Field.Index),
             new("Spread", V2Field.Spread), new("Venue clock", V2Field.VenueClock),
             new("Last trade", V2Field.LastTrade)]),

        new("liquidity", "Liquidity", "base units · bid / ask at 25 bps", PairColumn.Depth25, CallTone.Depth,
            [new("Bid size", V2Field.BidSize), new("Ask size", V2Field.AskSize),
             new("10 bps", V2Field.Depth10), new("25 bps", V2Field.Depth25),
             new("50 bps", V2Field.Depth50), new("Book reach", V2Field.BookReach)]),

        new("positions", "Positions", "base units · × multiplier applied", PairColumn.OpenInterest, CallTone.OpenInterest,
            [new("OI base", V2Field.OpenInterest), new("Multiplier", V2Field.Multiplier)]),

        new("carry", "Cost of carry", "per cent per day · venue interval normalised", null, CallTone.Ticker,
            [new("Per day", V2Field.FundingPerDay), new("Venue rate", V2Field.FundingRate),
             new("Interval", V2Field.FundingInterval)]),

        new("activity", "Activity", "quote asset · rolling 24 h", PairColumn.Turnover24h, CallTone.Ticker,
            [new("Turnover Q", V2Field.Turnover24h)]),

        new("stress", "Stress", "liquidations, last hour", null, CallTone.Ticker,
            [new("Volume", V2Field.LiquidationVolume), new("Unit", V2Field.LiquidationUnit)]),

        new("trust", "Trust", "age of the worst call on this row", null, CallTone.Ticker,
            [new("Price", V2Field.AgePrice), new("Depth", V2Field.AgeDepth), new("OI", V2Field.AgeOpenInterest)]),
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
    IReadOnlyList<V2FieldRef> Fields);

public sealed record V2FieldRef(string Label, V2Field Field);

/// <summary>Every figure the expanded groups can show. Not every venue answers every one.</summary>
public enum V2Field
{
    Last, Mark, Index, Spread, VenueClock, LastTrade,
    BidSize, AskSize, Depth10, Depth25, Depth50, BookReach,
    OpenInterest, Multiplier,
    FundingPerDay, FundingRate, FundingInterval,
    Turnover24h,
    LiquidationVolume, LiquidationUnit,
    AgePrice, AgeDepth, AgeOpenInterest,
}

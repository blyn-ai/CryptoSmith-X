using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Data;

/// <summary>
/// What each cell of the v2 table says. Every rule that decides between a figure, a dash and a zero
/// lives here rather than in the view, for the same reason RowCells does on the first page: the view
/// is one loop over cells that all look alike, and the judgements are where the compiler and the
/// tests can see them.
///
/// A NULL VALUE IS A DASH AND NEVER A ZERO. That is the whole contract of this file. A venue that
/// does not publish a metric, a call that has never landed, and a measured zero are three different
/// states, and the first two are the same dash only because we cannot tell them apart from one row.
/// </summary>
public static class V2Cells
{
    public static V2Cell Headline(VenueRowModel r, PairColumn column) => column switch
    {
        PairColumn.SpreadBps => Fig(r.Row.SpreadBps, 2, sub: Pair(r.Row.BidPrice, r.Row.AskPrice, 4)),
        // Сумма сторон крупно, пара мелко. Раньше крупным стоял bid, то есть ровно левая
        // половина того, что написано под ним — одно число дважды и ни одного про всю книгу.
        PairColumn.Depth25 => Fig(Sum(r.Row.DepthBid25, r.Row.DepthAsk25), 0,
            sub: Pair(r.Row.DepthBid25, r.Row.DepthAsk25, 0)),
        PairColumn.OpenInterest => Fig(r.Row.OpenInterest, 0),
        PairColumn.Turnover24h => Fig(r.Row.Turnover24h, 0, sub: r.Row.QuoteAsset),
        _ => V2Cell.None,
    };

    /// <summary>The two groups with no single comparable figure: carry is a rate normalised per the
    /// venue's own interval, and trust is an age. Neither ranks against a maximum, so neither gets a
    /// bar — a bar across incomparable units is a picture that looks right and is not.</summary>
    public static V2Cell Headline(VenueRowModel r, V2Group g, StressRow? stress = null) => g.Key switch
    {
        "carry" => Field(r, V2Field.FundingPerDay),
        "trust" => Worst(r),
        "stress" => stress is null ? V2Cell.None : new V2Cell(stress.Volume, Format.Num(stress.Volume, 0), stress.Unit),
        _ => V2Cell.None,
    };

    public static V2Cell Field(VenueRowModel r, V2Field f, StressRow? stress = null) => f switch
    {
        // Единица агрегата приходит СТРОКОЙ из базы: площадки считают объём ликвидаций
        // по-разному, и подставлять свою единицу значило бы переименовать чужую величину.
        V2Field.LiquidationVolume => stress is null ? V2Cell.None : new V2Cell(stress.Volume, Format.Num(stress.Volume, 0), null),
        V2Field.LiquidationUnit => stress is null ? V2Cell.None : Text(stress.Unit),

        V2Field.Last => Fig(r.Row.LastPrice, 6),
        V2Field.Mark => Fig(r.Row.MarkPrice, 8),
        V2Field.Index => Fig(r.Row.IndexPrice, 8),
        V2Field.Spread => Fig(r.Row.SpreadBps, 2),
        // Часы БИРЖИ, а не наши: received_at — это когда МЫ получили кадр.
        V2Field.VenueClock => r.Row.VenueTs is { } vt ? Text(Format.UtcClock(vt)) : V2Cell.None,

        // Настоящий момент сделки. Стояла подстановка received_at — то есть страница печатала
        // время нашего опроса под видом времени сделки и утверждала то, чего не измеряла.
        // У Hyperliquid здесь всегда прочерк, и это правда: его last_price — mid книги.
        V2Field.LastTrade => r.Row.LastTradeAt is { } lt ? Text(Format.UtcClock(lt)) : V2Cell.None,

        V2Field.BidSize => Fig(r.Row.BidSize, 0),
        V2Field.AskSize => Fig(r.Row.AskSize, 0),
        V2Field.Depth10 => Fig(Sum(r.Row.DepthBid10, r.Row.DepthAsk10), 0, sub: Pair(r.Row.DepthBid10, r.Row.DepthAsk10, 0)),
        V2Field.Depth25 => Fig(Sum(r.Row.DepthBid25, r.Row.DepthAsk25), 0, sub: Pair(r.Row.DepthBid25, r.Row.DepthAsk25, 0)),
        V2Field.Depth50 => Fig(Sum(r.Row.DepthBid50, r.Row.DepthAsk50), 0, sub: Pair(r.Row.DepthBid50, r.Row.DepthAsk50, 0)),
        // «Докуда видна книга», обе стороны. Ноль — это измеренный ноль (сторона пуста), и он
        // печатается как 0, а не как прочерк: прочерк значит «не мерили».
        V2Field.BookReach => r.Row.BookReachBid is null && r.Row.BookReachAsk is null
            ? V2Cell.None
            : new V2Cell(r.Row.BookReachBid, "±" + Format.Num(Math.Max(r.Row.BookReachBid ?? 0, r.Row.BookReachAsk ?? 0), 0) + " bps",
                Format.Num(r.Row.BookReachBid, 0) + " / " + Format.Num(r.Row.BookReachAsk, 0)),

        V2Field.OpenInterest => Fig(r.Row.OpenInterest, 0),
        V2Field.Multiplier => Text(r.Row.ContractMultiplier == 1 ? "1 : 1" : "× " + Format.Num(r.Row.ContractMultiplier, 0)),

        // Normalised to a day so seven venues on three different intervals can be read down one
        // column. The venue's own rate and its interval stay visible beside it, because the
        // normalisation is ours and the reader is entitled to the number the venue published.
        V2Field.FundingPerDay => PerDay(r),
        V2Field.FundingRate => Pct(r.Row.FundingRate),
        V2Field.FundingInterval => r.Row.FundingIntervalHours is { } h ? Text(h + " h") : V2Cell.None,

        V2Field.Turnover24h => Fig(r.Row.Turnover24h, 0, sub: r.Row.QuoteAsset),

        V2Field.AgePrice => Age(r.Ages.PriceSeconds),
        V2Field.AgeDepth => Age(r.Ages.DepthSeconds),
        V2Field.AgeOpenInterest => Age(r.Ages.OpenInterestSeconds),
        _ => V2Cell.None,
    };

    /// <summary>The oldest of the three calls behind the row — the trust column's headline, because a
    /// row is only as current as its stalest ingredient.</summary>
    private static V2Cell Worst(VenueRowModel r)
    {
        double? worst = null;
        foreach (var a in new[] { r.Ages.PriceSeconds, r.Ages.DepthSeconds, r.Ages.OpenInterestSeconds })
        {
            if (a is { } v && (worst is null || v > worst))
            {
                worst = v;
            }
        }

        return Age(worst);
    }

    private static V2Cell PerDay(VenueRowModel r)
    {
        if (r.Row.FundingRate is not { } rate || r.Row.FundingIntervalHours is not { } hours || hours <= 0)
        {
            return V2Cell.None;
        }

        return new V2Cell(rate, Format.SignedPercent(rate * (24.0 / hours), 4), "per day · " + hours + " h interval");
    }

    private static V2Cell Pct(double? v) =>
        v is null ? V2Cell.None : new V2Cell(v, Format.SignedPercent(v, 4), null);

    private static V2Cell Age(double? seconds) =>
        seconds is null ? V2Cell.None : new V2Cell(seconds, Format.ShortAge(seconds), null);

    private static V2Cell Text(string s) =>
        string.IsNullOrEmpty(s) || s == "—" ? V2Cell.None : new V2Cell(0, s, null);

    private static V2Cell Fig(double? v, int decimals, string? sub = null) =>
        v is null ? V2Cell.None : new V2Cell(v, Format.Num(v, decimals), sub);

    private static double? Sum(double? a, double? b) =>
        a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static string? Pair(double? a, double? b, int decimals) =>
        a is null && b is null ? null : Format.Num(a, decimals) + " / " + Format.Num(b, decimals);
}

/// <summary>One cell: the number it ranks by (null = not measured), what it prints, and the small
/// line under it.</summary>
public sealed record V2Cell(double? Value, string Text, string? Sub)
{
    public static readonly V2Cell None = new(null, "—", null);
}

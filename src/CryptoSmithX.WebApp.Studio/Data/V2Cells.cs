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
    /// <summary>
    /// The collapsed cell, with its ranks stated.
    ///
    /// <b>Marks belong here and not only inside an opened group.</b> The collapsed table is the page
    /// as it is opened, and until now it computed who held the best bid and said nothing about it
    /// until the reader clicked. A rank the page knows and withholds is a rank it did not make.
    ///
    /// <b>Both ends or neither.</b> That is Verdicts' rule and it is not restated here — the table
    /// simply hands back None for a figure with no rank, which is what a lone winner over a field of
    /// one, or a figure whose call has gone degraded, produces.
    /// </summary>
    public static V2Head Head(VenueRowModel r, V2Group g, VerdictTable v, StressRow? stress = null)
    {
        var id = r.Row.InstrumentId;

        switch (g.Key)
        {
            case "price":
                // КРУПНО — ЦЕНА. Здесь стоял спред, и колонка под шапкой PRICE начиналась с «4.61»:
                // читатель, ведущий глазом по колонке цен, встречал число, которое ценой не
                // является. Спред ушёл в мелкую строку, где у него своя пара слов — TIGHT и WIDE.
                return new V2Head(
                    [
                        Part(r.Row.BidPrice, Format.PriceDecimals(r.Row), V2Mark.Of(v, id, PairColumn.Bid), column: PairColumn.Bid),
                        Part(r.Row.AskPrice, Format.PriceDecimals(r.Row), V2Mark.Of(v, id, PairColumn.Ask), column: PairColumn.Ask),
                    ],
                    [Part(r.Row.SpreadBps, 2, V2Mark.Of(v, id, PairColumn.SpreadBps, spread: true), suffix: " bps", column: PairColumn.SpreadBps)],
                    r.Row.SpreadBps,
                    PairColumn.SpreadBps);

            case "liquidity":
                // Здесь наоборот, и это не то же правило: сумма сторон — сравнимая величина и стоит
                // крупно, а половины под ней отвечают на другой вопрос — куда книга наклонена, — и
                // ранжируются отдельно от суммы и друг от друга.
                return new V2Head(
                    [Part(Sum(r.Row.DepthBid25, r.Row.DepthAsk25), 0, V2Mark.Of(v, id, PairColumn.Depth25), column: PairColumn.Depth25)],
                    [
                        Part(r.Row.DepthBid25, 0, V2Mark.Of(v, id, PairColumn.Depth25Bid), column: PairColumn.Depth25Bid),
                        Part(r.Row.DepthAsk25, 0, V2Mark.Of(v, id, PairColumn.Depth25Ask), column: PairColumn.Depth25Ask),
                    ],
                    Sum(r.Row.DepthBid25, r.Row.DepthAsk25),
                    PairColumn.Depth25);

            case "positions":
                return new V2Head(
                    [Part(r.Row.OpenInterest, 0, V2Mark.Of(v, id, PairColumn.OpenInterest), column: PairColumn.OpenInterest)],
                    [],
                    r.Row.OpenInterest,
                    PairColumn.OpenInterest);

            case "activity":
                return new V2Head(
                    [Part(r.Row.Turnover24h, 0, V2Mark.Of(v, id, PairColumn.Turnover24h), column: PairColumn.Turnover24h)],
                    [new V2Part(r.Row.QuoteAsset)],
                    r.Row.Turnover24h,
                    PairColumn.Turnover24h);

            case "carry":
                // Нормировка на сутки — НАША, а не биржи, поэтому ранга здесь нет: пометить
                // «лучшую» стоимость переноса значило бы ранжировать наше приведение.
                var day = Field(r, V2Field.FundingPerDay);
                return new V2Head(
                    [new V2Part(day.Text, null, day.Value is not null)],
                    day.Sub is null ? [] : [new V2Part(day.Sub)],
                    null, null);

            case "stress":
                // Единица стоит РЯДОМ С ЧИСЛОМ (правило 9), а не отдельной строкой полей: у площадок
                // она разная, и это свойство числа, а не вторая величина. И ранга здесь нет по той
                // же причине — «base» у одной и «quote-notional» у другой не сравниваются.
                return stress is null
                    ? new V2Head([new V2Part("—", null, false)], [], null, null)
                    : new V2Head(
                        [new V2Part(Format.Num(stress.Volume, 0))],
                        [new V2Part(stress.Unit)],
                        null, null);

            default:
                return new V2Head([new V2Part("—", null, false)], [], null, null);
        }
    }

    private static V2Part Part(
        double? value, int decimals, V2Mark? mark = null, string suffix = "", PairColumn? column = null) =>
        value is null
            ? new V2Part("—", null, false, column)
            : new V2Part(Format.Num(value, decimals) + suffix, mark, true, column);


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

    public static V2Cell Field(VenueRowModel r, V2Field f, StressRow? stress = null, BookFrame? book = null) => f switch
    {
        // Единица агрегата приходит СТРОКОЙ из базы: площадки считают объём ликвидаций
        // по-разному, и подставлять свою единицу значило бы переименовать чужую величину.
        V2Field.LiquidationVolume => stress is null ? V2Cell.None : new V2Cell(stress.Volume, Format.Num(stress.Volume, 0), null),
        V2Field.LiquidationUnit => stress is null ? V2Cell.None : Text(stress.Unit),

        // Бид и аск ОТДЕЛЬНЫМИ цифрами, а не только парой под спредом: ранг у них считается по
        // каждой стороне, и одна площадка бывает лучшей по биду и худшей по аску одновременно —
        // ровно то, что общая подпись «bid / ask» показать не могла.
        V2Field.Bid => Fig(r.Row.BidPrice, 6),
        V2Field.Ask => Fig(r.Row.AskPrice, 6),
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
        // «Докуда видна книга» — СЧИТАЕТСЯ ЗДЕСЬ, из того самого кадра, который рисует полоса 2,
        // а не берётся из колонок 0030. Оттуда приходило ±108 734 bps (десятикратная цена: где-то
        // абсолютная цена принята за bps) и ±0 bps при двадцати пяти уровнях, чего не бывает
        // физически. Обе цифры печатались как измерение.
        V2Field.BookReach => Reach(book),

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

    /// <summary>Максимальное правдоподобное удаление уровня от середины. Дальше — не глубокая
    /// книга, а сломанный кадр: 5 000 bps это половина цены, и стакан, тянущийся на половину
    /// цены, мы ещё не видели ни на одной площадке.</summary>
    private const double ReachCeilingBps = 5000;

    /// <summary>Public because BAND 2 PRINTS THE SAME SENTENCE under its ladder. It said "reach
    /// not measured" for a listing whose band-1 cell read ±18 bps — one page, one book, two
    /// answers — because the two were computed from different sources.</summary>
    public static V2Cell Reach(BookFrame? book)
    {
        if (book is null || book.BidPx.Length == 0 || book.AskPx.Length == 0)
        {
            return V2Cell.None;
        }

        var mid = (book.BidPx.Max() + book.AskPx.Min()) / 2;
        if (mid <= 0)
        {
            return V2Cell.None;
        }

        var bid = (mid - book.BidPx.Min()) / mid * 10000;
        var ask = (book.AskPx.Max() - mid) / mid * 10000;
        var far = Math.Max(bid, ask);

        return far <= 0 || far > ReachCeilingBps
            ? V2Cell.None
            : new V2Cell(far, "±" + Format.Num(far, 0) + " bps",
                Format.Num(bid, 0) + " / " + Format.Num(ask, 0));
    }

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

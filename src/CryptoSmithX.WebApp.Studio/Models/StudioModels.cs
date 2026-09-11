using CryptoSmithX.WebApp.Studio.Data;

namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// One row of the pair list: a pair as the site addresses it, after the display-only fold from
/// 0024. Both halves are FAMILY codes, not asset codes — BTC/USD here can be BTC/USDT on one
/// venue and BTC/USD on another, and the comparison page says which is which per row.
/// </summary>
/// <param name="Venues">
/// How many segments carry it. Distinct segments rather than distinct exchanges, because a
/// segment is what has a book: kraken-futures and a future kraken-spot quote the same pair from
/// two different order books and are two rows on the comparison page, not one.
/// </param>
/// <param name="Listings">
/// How many instruments fold into it, which is NOT the same number. One segment can list
/// BTC/USDT and BTC/USDC at once; folding the quotes puts both on one page, and both keep their
/// own book, their own spread and their own quote asset. Publishing only <paramref name="Venues"/>
/// would quietly claim a venue has one book for the pair when it has two.
/// </param>
public sealed record PairListItem(string BaseFamily, int Venues, int Listings);

/// <summary>
/// One board's worth of that list: the cards themselves, how many pairs the filter matched, and the
/// ceiling that cut them.
///
/// The three travel together deliberately. A caller holding only the cards cannot tell a full board
/// from a complete one, and the difference is the whole reason the limit is allowed to exist: the
/// page shows the first <see cref="Limit"/> and states how many <see cref="Matching"/> there were.
/// </summary>
public sealed record PairListPage(IReadOnlyList<PairListItem> Items, int Matching, int Limit);

/// <summary>
/// One venue's listing of the pair, exactly as <c>market_snapshot_latest</c> holds it.
///
/// Everything nullable here is nullable in the schema and nowhere else: 0001 declares last, bid,
/// ask, the two sizes, mark, index, funding, turnover, open interest and open_interest_at NOT
/// NULL, so on Studio a dash can only ever appear in the six depth bands and depth_at (blueprint
/// §10, docs/datagaps.md). The market columns are nullable in THIS record for one reason — the
/// join to the snapshot is a LEFT JOIN, so an instrument discovery has listed but the collector
/// has never observed arrives with every market field null. That is a row that says "no
/// observation", which is a different sentence from "the book is empty", and it must not be
/// rendered as zeros.
///
/// <see cref="QuoteAsset"/> and <see cref="BaseAsset"/> are the venue's own spelling, never the
/// family. The heading folds; the row does not. That is the whole contract of 0024.
/// </summary>
public sealed record PairVenueRow(
    int InstrumentId,
    string SegmentCode,
    string SegmentKind,
    string ExchangeCode,
    string ExchangeName,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    /// <summary>The quote's family — USD for USD, USDT and USDC once the registry says so, and the
    /// quote's own code where it says nothing. Read from the registry rather than matched on the
    /// name, and used for one thing only: deciding which rows a notional may be ranked against.
    /// The row still PRINTS <see cref="QuoteAsset"/> everywhere; the family never appears as a
    /// figure's unit.</summary>
    string QuoteFamily,
    double ContractMultiplier,
    double PriceStep,
    double QtyStep,
    /// <summary>Hours between funding payments, or NULL where the venue never told us. Migration
    /// 0028 made the column nullable precisely so absence could travel; reading it as a
    /// non-nullable short would turn a null into a zero and the page would print a rate normalised
    /// by nothing.</summary>
    short? FundingIntervalHours,
    string Status,
    DateTime StatusChangedAt,
    DateTime FirstSeenAt,
    DateTime? ReceivedAt,
    double? LastPrice,
    double? BidPrice,
    double? AskPrice,
    double? BidSize,
    double? AskSize,
    double? MarkPrice,
    double? IndexPrice,
    double? FundingRate,
    double? Turnover24h,
    double? OpenInterest,
    DateTime? OpenInterestAt,
    double? DepthBid10,
    double? DepthAsk10,
    double? DepthBid25,
    double? DepthAsk25,
    double? DepthBid50,
    double? DepthAsk50,
    DateTime? DepthAt,

    /// <summary>Время тикера по бирже; NULL там, где площадка его не проставляет.</summary>
    DateTime? VenueTs,

    /// <summary>Момент сделки, которой соответствует LastPrice. У Hyperliquid ВСЕГДА NULL:
    /// его last_price — это mid книги, а не сделка.</summary>
    DateTime? LastTradeAt,

    double? FundingRatePredicted,
    DateTime? NextFundingAt,

    /// <summary>Оборот 24 ч в БАЗЕ. Turnover24h остаётся в котировке; одно из другого не
    /// выводится без цены на каждый момент внутри окна, а не только на его конец.</summary>
    double? Volume24hBase,

    /// <summary>Mid, ОТ которого отсчитаны полосы глубины. Без него полосы невоспроизводимы.</summary>
    double? DepthRef,

    /// <summary>Докуда видна сторона книги, в bps от DepthRef. Пустая сторона — 0, а не NULL:
    /// измерение состоялось и дало «пусто». NULL — глубину не собирали.</summary>
    double? BookReachBid,
    double? BookReachAsk,

    /// <summary>Открытый интерес в КОТИРУЕМОМ активе, как публикует площадка. Парная величина к
    /// <see cref="OpenInterest"/> (количество); ни одна из другой не выводится.</summary>
    double? OiQuote,

    /// <summary>Торгуется ли инструмент прямо сейчас, по заявлению площадки. NULL = площадка
    /// такого флага не публикует — и тогда судим по возрасту, как раньше: неизвестная сессия не
    /// оправдание.</summary>
    bool? MarketOpen,

    /// <summary>Как устроен рынок сегмента: 'orderbook' или 'oracle_vault'. Не наблюдение —
    /// свойство площадки, и именно оно объясняет, почему у строки пусты bid/ask, размеры, полосы
    /// глубины и пара фандинга: «нет по природе», а не «не получили».</summary>
    string MarketModel)
{
    /// <summary>
    /// The spread in basis points of the mid. Quote-free by construction, which is why it is the
    /// one price-derived figure that ranks across the whole pair rather than within one quote.
    ///
    /// A crossed book (bid above ask) yields a negative number and is returned as such: 0001 says
    /// a crossed book is written as it stands because it is a fact, and clamping it here would be
    /// this page inventing a market state the collector explicitly refused to invent.
    /// </summary>
    public double? SpreadBps
    {
        get
        {
            if (BidPrice is not { } bid || AskPrice is not { } ask)
            {
                return null;
            }

            var mid = (bid + ask) / 2.0;
            return mid > 0 ? (ask - bid) / mid * 10_000.0 : null;
        }
    }

    /// <summary>
    /// The funding rate normalised to a day, and it is only ever printed under that label. The
    /// venues do not agree on the interval — weex alone runs 4 instruments hourly, 480 at four
    /// hours and 539 at eight — so the raw rates are not comparable figures, and a column of them
    /// side by side without the interval beside each one would be a comparison of four different
    /// things. It carries no verdict either way; see <see cref="CryptoSmithX.WebApp.Studio.Data.Verdicts"/>.
    /// </summary>
    public double? FundingRatePerDay =>
        FundingRate is { } rate && FundingIntervalHours > 0
            ? rate * (24.0 / FundingIntervalHours)
            : null;

    /// <summary>Total notional inside the 10bps band, both sides. Null unless BOTH sides were
    /// measured: a band with one side missing is not a smaller book, it is a partial measurement,
    /// and summing it against a complete one would rank a gap as a number.</summary>
    public double? Depth10 => Both(DepthBid10, DepthAsk10);

    /// <inheritdoc cref="Depth10"/>
    public double? Depth25 => Both(DepthBid25, DepthAsk25);

    /// <inheritdoc cref="Depth10"/>
    public double? Depth50 => Both(DepthBid50, DepthAsk50);

    private static double? Both(double? bid, double? ask) =>
        bid is { } b && ask is { } a ? b + a : null;
}

/// <summary>
/// One venue's row with the windows the three calls behind it are judged against.
///
/// Kept as a wrapper rather than as fields on <see cref="PairVenueRow"/> because the row is what
/// Dapper builds out of the snapshot and the window is what the segment's configuration and its
/// observed pass say about it. Two sources, two records, and no way to read one and forget the
/// other.
/// </summary>
public sealed record PairVenue(PairVenueRow Row, FreshnessWindows Windows, CallCadence Cadence);

/// <summary>
/// One pair across every venue that lists it, at the instant the queries ran.
///
/// There is deliberately no "now" in here. The instants are absolute, so a payload served from a
/// 900 ms-old cache still reports a truthful age — but only if the age is computed against the
/// time of the REQUEST, and the only way to guarantee that is for the cached object to be
/// incapable of carrying a clock. Blueprint §5.
///
/// The verdict table used to live here and no longer does, for that same rule. A rank is now
/// withheld from a figure whose call has gone <c>degraded</c>, and "degraded" is a subtraction
/// against the time of the request — so a verdict table computed as the cache filled would be a
/// judgement about freshness frozen at fill time, carried inside the one object that is forbidden
/// to carry a clock. <see cref="CryptoSmithX.WebApp.Studio.Data.Verdicts.Compute"/> is called per request,
/// beside the ages it now depends on.
/// </summary>
public sealed record AssetComparison(
    string BaseFamily,
    IReadOnlyList<PairVenue> Venues)
{
    /// <summary>Rows. One order book on one venue with one quote — Binance's PEPE/USDT and
    /// PEPE/USDC are two of them.</summary>
    public int Listings => Venues.Count;

    /// <summary>Distinct venues. Smaller than <see cref="Listings"/> whenever a venue runs more
    /// than one book on the asset, and the header must never print one number for both.</summary>
    public int VenueCount => Venues.Select(v => v.Row.SegmentCode).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Distinct platforms — the company behind the venues. Kraken's futures and a future
    /// Kraken spot are two venues on one platform, so this is a third number again.</summary>
    public int PlatformCount => Venues.Select(v => v.Row.ExchangeCode).Distinct(StringComparer.Ordinal).Count();

    /// <summary>The quotes present, in the order the rows carry them. The page defaults to all of
    /// them together; a filter over this set is an option, never the address.</summary>
    public IReadOnlyList<string> Quotes =>
        Venues.Select(v => v.Row.QuoteAsset).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// The three windows a row's three calls are judged against — price, open interest and the depth
/// sweep, because they are three calls with three clocks and one number for all three is the bug
/// this record exists to make unrepresentable.
///
/// Null means the window is not known, which happens only when the cascade yields no interval for
/// that dataset on that segment. A null window is not a licence to invent one: nothing fades,
/// nothing carries △, and the age is printed bare. A guessed window would be a claim about how
/// often we look, made up by the page that is supposed to be reporting it.
/// </summary>
public sealed record FreshnessWindows(double? PriceSeconds, double? OpenInterestSeconds, double? DepthSeconds)
{
    public static readonly FreshnessWindows Unknown = new(null, null, null);
}

/// <summary>
/// The three calls' own configured cadence — the bare interval, with no measured pass folded in.
///
/// Kept separate from <see cref="FreshnessWindows"/> rather than read back out of it: the window is
/// already cadence plus headroom, and a late-threshold check that wants the cadence ALONE (see
/// <see cref="Data.Freshness.LateCadenceMultiplier"/>) needs the number before that headroom was
/// added, not after.
/// </summary>
public sealed record CallCadence(double? PriceSeconds, double? OpenInterestSeconds, double? DepthSeconds)
{
    public static readonly CallCadence Unknown = new(null, null, null);
}

/// <summary>
/// What one segment's three calls are configured to do, and how wide a pass over that segment
/// actually is.
///
/// The cadences come from the cascade <c>segment_dataset.interval_s → dataset.default_interval_s</c>
/// (10 s for snapshot, 60 s for depth as seeded by 0014). The pass figures are measured from the
/// data itself, and the reason they exist at all is the reason this whole record exists: a call
/// that visits one symbol at a time does not refresh every instrument every interval. It refreshes
/// each instrument once per PASS, and on WEEX one depth pass over 1,005 instruments was measured at
/// 361 s (0021). Judged against the bare 60 s interval, nine out of ten healthy WEEX depth cells
/// carry △ and the page prints "degraded" over a collector doing exactly what we configured it to
/// do. That already happened once — <c>StaleThresholdTests</c> exists because a literal 30 s made
/// Kraken read degraded at 39 s — and doing it again on the public page is the one outcome this
/// step was told twice to avoid.
/// </summary>
/// <param name="PricePassSeconds">
/// How far behind the segment's freshest row the 95th-percentile row is, per call. Measured, not
/// reported: <c>collector_status.avg_duration_ms</c> holds a sweep duration the collector wrote
/// about itself, and 0025 deliberately withholds that grant, because substituting a collector's
/// self-report for the age of the data is precisely what produced the Kraken incident. This number
/// is a dispersion across instruments at one instant, not an age, and that is what makes it safe:
/// when collection stops, every timestamp on the segment freezes together, the dispersion stays
/// where it was, ages keep growing, and the cells go stale exactly as they should. A window that
/// widened to swallow an outage would be worse than a flat one.
/// </param>
public sealed record SegmentFreshness(
    string SegmentCode,
    int? SnapshotIntervalSeconds,
    int? DepthIntervalSeconds,
    int? OpenInterestIntervalSeconds,
    double? PricePassSeconds,
    double? OpenInterestPassSeconds,
    double? DepthPassSeconds)
{
    /// <summary>
    /// The widest pass this system is willing to treat as "still arriving", as a multiple of the
    /// call's own cadence. Borrowed from rule 2 rather than invented: twelve windows is already
    /// where the design system stops counting and prints the word <c>degraded</c>, so it is also
    /// the furthest the page will stretch its idea of new before it starts saying old.
    ///
    /// It closes the one hole in a measured dispersion. If a venue stops serving the book for a
    /// handful of symbols while the rest of the pass keeps cycling, those frozen rows drag the
    /// percentile up and would otherwise widen the window for every other cell on the page. The
    /// percentile handles a few; the cap handles many.
    /// </summary>
    public const double PassCapWindows = 12.0;

    public FreshnessWindows Windows => new(
        Window(SnapshotIntervalSeconds, PricePassSeconds),
        // Open interest rides the snapshot ticker on every venue we run today — 0014 disables the
        // open_interest dataset everywhere with the note "carried inline in the snapshot ticker" —
        // so its cadence falls back to the snapshot's when it has no loop of its own. It still gets
        // its own window and its own age, because Binance fetches it per symbol (~60 s, 0001) and
        // open_interest_at exists precisely so that difference is visible rather than averaged away.
        Window(OpenInterestIntervalSeconds ?? SnapshotIntervalSeconds, OpenInterestPassSeconds),
        Window(DepthIntervalSeconds, DepthPassSeconds));

    /// <summary>The bare cadence behind each call, with no pass-derived headroom — the same
    /// open-interest fallback <see cref="Windows"/> uses, for the same reason.</summary>
    public CallCadence Cadence => new(
        SnapshotIntervalSeconds,
        OpenInterestIntervalSeconds ?? SnapshotIntervalSeconds,
        DepthIntervalSeconds);

    /// <summary>
    /// The window one call is judged against: how often it runs, plus how long one pass over this
    /// segment takes. Both halves come from the database — the first from configuration, the second
    /// from the spread of the timestamps that call has written — and neither is a literal. A call
    /// with no configured cadence has no window; see <see cref="FreshnessWindows"/>.
    /// </summary>
    public static double? Window(int? cadenceSeconds, double? passSeconds)
    {
        if (cadenceSeconds is not { } cadence || cadence <= 0)
        {
            return null;
        }

        // The floor is one whole cadence, and it is not slack. A call that runs every ten seconds
        // leaves its freshest row ageing from zero to ten before the next one lands: that range IS
        // healthy operation, not lateness. A window of exactly one cadence is therefore crossed by
        // a collector doing precisely what it was configured to do, roughly half the time — and on
        // a page whose ages count forward from the render, it is crossed by the reader simply
        // looking at it for three seconds. Measured on production: every segment reports a
        // 95th-percentile pass of 0.0-0.8 s, so every ticker window was 10-10.8 s and the whole
        // page filled with △ while it sat open.
        //
        // Only past TWO cadences has a poll actually been missed, and that is the first moment the
        // page is entitled to say so. This is the same mistake as the flat 30 s that made Kraken
        // read degraded at 39 s, mirrored: that one judged a WIDE pass against a literal, this one
        // judged a TIGHT one against no headroom at all.
        var pass = Math.Max(passSeconds ?? 0, 0);
        return cadence + Math.Min(Math.Max(pass, cadence), cadence * PassCapWindows);
    }
}

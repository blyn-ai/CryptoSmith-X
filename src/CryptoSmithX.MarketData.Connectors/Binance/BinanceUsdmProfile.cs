namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>Whether a venue publishes its own open-interest history, or whether the Hub has to build
/// one by sampling the live figure. Binance answers <c>/futures/data/openInterestHist</c>; Aster's
/// equivalent path is a 404 HTML page (verified live), so its profile says <see cref="Sampled"/> and
/// <see cref="BinanceUsdmMarketData.GetOpenInterestHistoryAsync"/> returns an empty list without ever
/// calling the venue — the documented "no history" answer, which
/// <c>OpenInterestHistoryCollector</c>'s sampled branch already knows how to bucket itself, exactly as
/// it does for WEEX.</summary>
public enum OpenInterestHistoryMode
{
    Analytics,
    Sampled,
}

/// <summary>What a WS feed subscribes. Both venues subscribe only what discovery has marked
/// <c>collect = true</c>: a stream nothing stores is pure cost. Binance subscribed its whole listing
/// until 2026-09-17 — ~566 depth and ~1 135 market streams for 44 collected symbols — and on the
/// 3-core test VPS that was the load Binance answered by closing the socket ~50 times an hour.
/// <see cref="WholeVenue"/> stays for a venue that needs its whole listing on the wire.
/// </summary>
public enum FeedSymbolsMode
{
    WholeVenue,
    Collected,
}

/// <summary>
/// The handful of constants that differ between Binance USDⓈ-M and a venue whose wire protocol is a
/// byte-for-byte clone of it — Aster today, and the shape a third clone would extend. See
/// <c>plans/aster-venue-blueprint.md</c> §4.2 for the full argument; this record is its
/// implementation.
///
/// <b>Binance keeps its current values byte for byte.</b> Every field below either reproduces a
/// constant that used to live directly on <see cref="BinanceUsdmClient"/> or
/// <see cref="BinanceUsdmMarketData"/>, or reproduces the behaviour those classes already had before
/// this record existed. <see cref="Binance"/> is not a default guessed to look reasonable; it is
/// those old values, named, and <c>BinanceUsdmProfileTests</c> pins each one against the literal it
/// replaced so a later edit aimed at Aster cannot silently move Binance.
///
/// <b>Not a base class.</b> The classes that read this record stay sealed and unforked — the profile
/// is data passed into existing constructors, not a new type in a new hierarchy. See the blueprint's
/// "alternatives considered and rejected" for why: a copy of ~1900 lines of feed code in
/// <c>Connectors/Aster/</c> would drift from this one the first time either is fixed, and a
/// <c>BinanceCompatibleMarketData</c> base class would expose this adapter's private machinery as
/// protected surface for exactly one second consumer.
/// </summary>
public sealed record BinanceUsdmProfile
{
    /// <summary>What <see cref="BinanceUsdmMarketData.SegmentCode"/> reports, and the key every
    /// collector query, capability row and log line downstream is keyed on.</summary>
    public required string SegmentCode { get; init; }

    /// <summary>The logger category the Hub's <c>ExchangeWorker.BuildBinance</c> builds this
    /// adapter's logger under — "Binance.Usdm" today, unchanged; "Aster.Perp" for the new segment,
    /// so its log lines are never mistaken for Binance's in a shared process.</summary>
    public required string LogName { get; init; }

    /// <summary>The venue's name as the two WS feeds print it and as their logger categories start
    /// ("Binance.Ws", "Aster.Ws"), so an alert about one venue's socket never reads as another's.
    /// "Binance" reproduces the categories and wording those feeds always had.</summary>
    public required string FeedName { get; init; }

    /// <summary>The REST path prefix every endpoint is built under. Both venues answer identically
    /// under <c>/fapi/v1</c> today (verified live, blueprint §1.5); the field exists so a future move
    /// to Aster's "recommended" <c>/fapi/v3</c> — which answers every market-data path this adapter
    /// reads, identically — is a profile edit, not a fork.</summary>
    public required string ApiPrefix { get; init; }

    /// <summary>The scope rule discovery, and both WS feeds, filter symbols by. Binance's rule is
    /// <see cref="BinanceMarkets.IsInScope"/> unchanged; Aster's is the same rule narrowed by
    /// <c>symbolType == 0</c>, which keeps out the 133 equity/ETF/commodity/FX perpetuals the venue
    /// otherwise admits under the same <c>contractType = PERPETUAL</c> Binance uses for real crypto.
    /// </summary>
    /// <remarks><c>internal</c>, not <c>public</c>, unlike every other member here: its type names
    /// <see cref="BinanceSymbol"/>, which is deliberately internal (an implementation detail DTO, not
    /// part of this assembly's public surface). Only the Binance/Aster classes in this assembly ever
    /// call it; the Hub, which references <see cref="BinanceUsdmProfile"/> itself to pick a profile,
    /// never reads this member. Not <c>required</c> either, for the same visibility rule (a required
    /// member cannot be less visible than its type) — both profiles below set it regardless, and
    /// <c>BinanceUsdmProfileTests</c> would fail loudly if either one left it null.</remarks>
    internal Func<BinanceSymbol, bool> IsInScope { get; init; } = null!;

    /// <summary>Which <c>symbolType</c> values <see cref="IsInScope"/> is known to exclude ON
    /// PURPOSE, so <see cref="BinanceUsdmMarketData"/> can tell "documented and deliberately out"
    /// (silent) from "a value nobody has classified yet" (logged once, the
    /// <see cref="BinanceUsdmMarketData.ReportUnknownContractType"/> pattern). Null on Binance, which
    /// does not read this field at all — so a Binance symbol (whose <see cref="BinanceSymbol.SymbolType"/>
    /// is always null, the field being absent from Binance's own JSON) never trips this check, and
    /// Binance's log output is provably unchanged. On Aster this is <c>{ 1 }</c>: <c>0</c> passes
    /// <see cref="IsInScope"/> and never reaches here, <c>1</c> is the documented RWA set (§1.2) and
    /// stays quiet, and anything else — a genuinely new value, or the field going missing — is
    /// reported once.</summary>
    public HashSet<int>? KnownExcludedSymbolTypes { get; init; }

    /// <summary>The REST order-book window <see cref="BinanceUsdmMarketData.GetOrderBookAsync"/>
    /// asks for. 100 on Binance (the historic constant: the cheapest limit that bounds all three
    /// bands on everything except BTC and ETH, on a 570-symbol REST sweep). 500 on Aster: the same
    /// measurement redone live against Aster's book (blueprint §1.2/§4.2) found limit=100 leaves
    /// BTC's 25 and 50 bps bands null, and 500 is the cheapest limit that bounds all three there.
    /// </summary>
    public required int RestDepthLimit { get; init; }

    /// <summary>Whether this venue publishes its own open-interest history. See
    /// <see cref="OpenInterestHistoryMode"/>.</summary>
    public required OpenInterestHistoryMode OpenInterestHistory { get; init; }

    /// <summary>What the WS feeds subscribe. See <see cref="FeedSymbolsMode"/>.</summary>
    public required FeedSymbolsMode FeedSymbols { get; init; }

    /// <summary>The streams-per-connection cap the feeds enforce, or null for none. <b>Null on
    /// Binance, deliberately</b>: its docs say 1024, but its market feed has always subscribed
    /// 3 + 2 × ~566 ≈ 1 135 streams on one connection, and enforcing 1024 there would silently drop the
    /// ~56 alphabetically-last symbols' trades and WS candles (XRP, XLM, WLD and ZEC among the
    /// collected ones). Whether Binance really honours more than 1024 is a separate question for a
    /// separate change; this record must not change Binance's behaviour. 200 on Aster (blueprint §1.4) —
    /// the collected universe of 26 keeps the depth feed at 26 streams and the market feed at
    /// 3 + 2×26 = 55, both comfortably under it, but the guard exists so growing the collected set
    /// past 98 symbols is a warning in the log rather than a silent disconnect loop.</summary>
    public required int? MaxStreamsPerConnection { get; init; }

    /// <summary>Whether <see cref="BinanceUsdmMarketData.GetTickersAsync"/>'s WS branch may be used
    /// at all. True on Binance: <c>!ticker@arr</c> and <c>!markPrice@arr@1s</c> push the WHOLE
    /// batched venue, so a healthy market feed replaces two REST calls. False on Aster: the same two
    /// streams there push only symbols whose statistics changed this second — an average of 7 a
    /// frame, 149 distinct in a 10-minute capture — so a WS-first ticker would silently drop quiet
    /// instruments (TRX, DOT, BCH) from the snapshot for long stretches with no gap recorded. The
    /// market feed still runs on Aster for trades, liquidations and WS candles; only the ticker path
    /// is REST-only.</summary>
    public required bool TickersFromMarketFeed { get; init; }

    /// <summary>Binance USDⓈ-M. Every field reproduces a constant the classes had before the profile
    /// existed, except <see cref="FeedSymbols"/>: Collected since 2026-09-17 (see
    /// <see cref="FeedSymbolsMode"/>). Pinned in <c>AsterProfileTests</c>.
    /// </summary>
    public static readonly BinanceUsdmProfile Binance = new()
    {
        SegmentCode = "binance-usdm",
        LogName = "Binance.Usdm",
        FeedName = "Binance",
        ApiPrefix = "/fapi/v1",
        IsInScope = BinanceMarkets.IsInScope,
        KnownExcludedSymbolTypes = null,
        RestDepthLimit = 100,
        OpenInterestHistory = OpenInterestHistoryMode.Analytics,
        FeedSymbols = FeedSymbolsMode.Collected,
        MaxStreamsPerConnection = null,
        TickersFromMarketFeed = true,
    };

    /// <summary>Aster (asterdex) — see <c>plans/aster-venue-blueprint.md</c>.</summary>
    public static readonly BinanceUsdmProfile Aster = new()
    {
        SegmentCode = "aster-perp",
        LogName = "Aster.Perp",
        FeedName = "Aster",
        ApiPrefix = "/fapi/v1",
        IsInScope = s => BinanceMarkets.IsInScope(s) && s.SymbolType == 0,
        KnownExcludedSymbolTypes = [1],
        RestDepthLimit = 500,
        OpenInterestHistory = OpenInterestHistoryMode.Sampled,
        FeedSymbols = FeedSymbolsMode.Collected,
        MaxStreamsPerConnection = 200,
        TickersFromMarketFeed = false,
    };
}

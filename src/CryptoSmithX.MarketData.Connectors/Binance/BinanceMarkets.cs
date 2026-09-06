using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Binance;

/// <summary>
/// The scope and identity rules for Binance USDⓈ-M, in one place because the REST adapter and the WS
/// feed both have to agree on them: a symbol the feed streams but discovery never listed is a book
/// nobody can store, and a symbol discovery lists but the feed never subscribes is depth that
/// silently stays on REST.
///
/// Two of the three rules below deal with the same underlying fact — Binance's <c>contractType</c>
/// and <c>status</c> are OPEN vocabularies that grow without notice — and they deliberately answer it
/// in opposite ways. The asymmetry is the point, so it is argued at each rule rather than here.
/// </summary>
internal static class BinanceMarkets
{
    /// <summary>
    /// The contract types we carry, as an ALLOWLIST. Not a denylist of the dated types, and the
    /// difference is not stylistic: the live API today returns <c>TRADIFI_PERPETUAL</c> — 191
    /// symbols, XAUUSDT, TSLAUSDT, NVDAUSDT — a value that appears in no Binance documentation we
    /// can cite. A denylist written from the documented vocabulary (PERPETUAL, CURRENT_QUARTER,
    /// NEXT_QUARTER) would have admitted every one of them without a word, and this service would
    /// have quietly started collecting equity and metal perpetuals into an asset table built for
    /// crypto. An allowlist cannot fail that way: an unknown type is out until somebody decides it
    /// is in, and <see cref="BinanceUsdmMarketData"/> logs each unknown type once per pass so the
    /// decision is prompted rather than deferred forever.
    /// </summary>
    private static readonly HashSet<string> CarriedContractTypes = new(StringComparer.Ordinal) { "PERPETUAL" };

    /// <summary>
    /// The quote assets of the V1 scope, exactly as <c>IExchangeMarketData</c> words it: "linear
    /// perpetuals with a USD-family quote". Live USDⓈ-M also quotes perpetuals in USD1 (a third-party
    /// stablecoin), in "U", and one in BTC — the last of which is an inverse contract wearing a
    /// linear venue's clothes. All three are out of scope, and they are excluded here rather than
    /// left to the segment's <c>quote_assets</c> column so that emptying that column in the console
    /// widens the venue's quote list without silently changing what KIND of contract we hold.
    /// </summary>
    private static readonly HashSet<string> UsdFamily = new(StringComparer.Ordinal) { "USD", "USDT", "USDC" };

    /// <summary>Binance's default funding interval, applied to a symbol that <c>/fapi/v1/fundingInfo</c>
    /// does not list — that endpoint carries only symbols whose terms deviate, so absence is a
    /// statement and not a gap. 117 of the 897 listed symbols were absent when this was captured.</summary>
    public const short DefaultFundingIntervalHours = 8;

    public static bool IsCarriedContract(BinanceSymbol s) => CarriedContractTypes.Contains(s.ContractType);

    public static bool IsInScope(BinanceSymbol s) => IsCarriedContract(s) && UsdFamily.Contains(s.QuoteAsset);

    /// <summary>
    /// The venue's listing state, mapped to ours — and a THROW on anything unrecognised, which is
    /// the opposite of what <see cref="IsCarriedContract"/> does with an unknown contract type.
    ///
    /// The two cases are not alike. An unknown contract type describes an instrument we never
    /// claimed: dropping it changes nothing about what we already track. An unknown STATUS arrives
    /// on an instrument we are already tracking, and the tempting fallback — omit it from this
    /// pass — is not a quiet no-op at all: <c>DiscoveryCollector</c> writes 'delisted' over anything
    /// missing from <c>delist_after_missed_discoveries</c> consecutive passes, so three silent
    /// omissions become a lifecycle event this venue never announced. Throwing fails the whole
    /// discovery pass instead, which returns before the delisting sweep runs, so nothing is written
    /// at all: the collector records ok=false, a <c>collector_gap</c> opens, and a person is asked
    /// what the new word means. Loud and wrong beats quiet and wrong when the quiet answer
    /// fabricates history.
    ///
    /// SETTLING and PENDING_TRADING both land on Halted — listed, not trading — which is exactly
    /// what WEEX's adapter does with a contract that has no live market, and for the same reason:
    /// <see cref="InstrumentStatus.Delisted"/> is the store's word to say, never an adapter's.
    /// </summary>
    public static InstrumentStatus Status(BinanceSymbol s) => s.Status switch
    {
        "TRADING" => InstrumentStatus.Trading,

        // Everything below is "listed, but you cannot trade it now". Binance publishes several
        // shades of winding-down and one of not-yet-started; none of them is a delisting we are
        // entitled to declare, and none of them is tradable.
        "PENDING_TRADING" => InstrumentStatus.Halted,
        "PRE_SETTLE" => InstrumentStatus.Halted,
        "SETTLING" => InstrumentStatus.Halted,
        "PRE_DELIVERING" => InstrumentStatus.Halted,
        "DELIVERING" => InstrumentStatus.Halted,
        "DELIVERED" => InstrumentStatus.Halted,
        "CLOSE" => InstrumentStatus.Halted,

        _ => throw new InvalidOperationException(
            $"Binance USDⓈ-M reported status '{s.Status}' for {s.Symbol}, which this adapter does not "
            + "know. Discovery fails rather than omitting the instrument: an omission would be written "
            + "as a delisting after delist_after_missed_discoveries passes, inventing a lifecycle event "
            + "the venue never announced. Map the new status in BinanceMarkets.Status."),
    };

    /// <summary>The lower-case spelling every WebSocket stream name uses ('BTCUSDT' → 'btcusdt').
    /// Unlike WEEX's two symbol generations this is a pure case fold on one identity, so it is
    /// safely reversible and the feed needs no lookup table — but it lives here anyway, with the
    /// rest of the identity rules, so the REST and WS sides cannot drift.</summary>
    public static string ToStream(string symbol) => symbol.ToLowerInvariant();
}

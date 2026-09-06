using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Binance;

// The JSON shapes of the Binance USDⓈ-M public REST responses the adapter reads (base host
// https://fapi.binance.com), captured from the live API into Fixtures/binance. Only the fields the
// adapter maps are declared.
//
// Binance reports every market number as a QUOTED STRING ("79919.20", not 79919.20) and every clock
// as an unquoted integer of milliseconds. That split is honoured literally below — a price is a
// string property and a timestamp is a long — so a change of shape at the venue fails at the
// boundary instead of arriving as a silently defaulted 0.

/// <summary>
/// The serializer options for EVERY Binance payload, REST and WebSocket alike, and the one thing in
/// this file worth reading twice.
///
/// It is NOT <see cref="JsonSerializerDefaults.Web"/>, which every other connector in this repository
/// uses. Web defaults turn on <c>PropertyNameCaseInsensitive</c>, and Binance's frames carry pairs of
/// fields whose names differ ONLY in case: a depth diff has <c>U</c> (first update id) beside
/// <c>u</c> (last update id), and a book ticker has <c>b</c>/<c>B</c> (bid price / bid quantity)
/// beside <c>a</c>/<c>A</c>. A type declaring both members of such a pair cannot be bound
/// case-insensitively at all — System.Text.Json on .NET 10 throws
/// <c>InvalidOperationException</c> the first time that converter is built, because two properties
/// would resolve from one name. It is not a mismapping that shows up as a wrong number in
/// production; it is a hard failure the moment the first frame arrives, which is precisely why it
/// must be settled here rather than discovered on the socket.
///
/// So: camelCase naming (which maps <c>BidPrice</c> → <c>bidPrice</c> for the long-named REST
/// fields) with case-sensitivity left ON, and an explicit <see cref="JsonPropertyName"/> wherever the
/// venue's own name is not camelCase. <see cref="JsonNumberHandling.AllowReadingFromString"/> is kept
/// from the Web preset because Binance really does quote numbers, and losing it would break every
/// numeric field at once.
/// </summary>
internal static class BinanceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>Response of <c>/fapi/v1/exchangeInfo</c> (weight 1). Also carries <c>rateLimits</c>, from
/// which the 2400 weight/minute IP budget quoted in migration 0023 was read live.</summary>
internal sealed record BinanceExchangeInfo
{
    public List<BinanceSymbol> Symbols { get; init; } = [];
}

/// <summary>
/// One listing on USDⓈ-M. <c>contractType</c> and <c>status</c> are both open vocabularies at this
/// venue — see <see cref="BinanceMarkets"/>, which is where each is turned into a decision and where
/// the reason for treating the two differently is argued.
/// </summary>
internal sealed record BinanceSymbol
{
    public string Symbol { get; init; } = "";
    public string ContractType { get; init; } = "";
    public string Status { get; init; } = "";
    public string BaseAsset { get; init; } = "";
    public string QuoteAsset { get; init; } = "";

    /// <summary>When the contract listed, ms since epoch. Real on every symbol observed live, so it
    /// feeds <c>listed_at</c> directly rather than through a null guard that has never fired.</summary>
    public long OnboardDate { get; init; }

    public List<BinanceFilter> Filters { get; init; } = [];

    /// <summary>The venue's own payload for this symbol, captured verbatim for <c>raw_json</c> — set
    /// by the client from the source element, never rebuilt from the fields above.</summary>
    [JsonIgnore] public string RawJson { get; init; } = "";
}

/// <summary>One entry of a symbol's <c>filters</c> array. Binance does not give the trading
/// increments their own fields: tick size lives in PRICE_FILTER, quantity step and minimum in
/// LOT_SIZE, and the minimum order value in MIN_NOTIONAL, each identified only by
/// <c>filterType</c>.</summary>
internal sealed record BinanceFilter
{
    public string FilterType { get; init; } = "";
    public string? TickSize { get; init; }
    public string? StepSize { get; init; }
    public string? MinQty { get; init; }
    public string? Notional { get; init; }
}

/// <summary>One row of <c>/fapi/v1/fundingInfo</c> (weight 0 — the response carries no
/// <c>x-mbx-used-weight</c> header at all). It lists only the symbols whose funding terms deviate
/// from the venue's defaults, so a symbol ABSENT here funds on the 8-hour default; 780 of 897 listed
/// symbols were present when this was captured, 455 of them on a 4-hour interval.</summary>
internal sealed record BinanceFundingInfo
{
    public string Symbol { get; init; } = "";
    public int FundingIntervalHours { get; init; }
}

/// <summary>One row of <c>/fapi/v1/ticker/bookTicker</c> with no symbol (weight 5) — the whole
/// venue's top of book, and the only batched source of bid/ask SIZE. Lists trading symbols only:
/// 764 of the 897 in exchangeInfo when captured.</summary>
internal sealed record BinanceBookTicker
{
    public string Symbol { get; init; } = "";
    public string BidPrice { get; init; } = "0";
    public string BidQty { get; init; } = "0";
    public string AskPrice { get; init; } = "0";
    public string AskQty { get; init; } = "0";
    public long Time { get; init; }
}

/// <summary>One row of <c>/fapi/v1/premiumIndex</c> with no symbol (documented weight 10; measured
/// +6 live) — mark price, index price and the current funding rate for the whole venue in one call.
/// <c>lastFundingRate</c> is already a fraction of notional per interval, which is what the schema
/// stores, so it needs no conversion (unlike Kraken's absolute rate).</summary>
internal sealed record BinancePremiumIndex
{
    public string Symbol { get; init; } = "";
    public string MarkPrice { get; init; } = "0";
    public string IndexPrice { get; init; } = "0";
    public string LastFundingRate { get; init; } = "0";
    public long Time { get; init; }
}

/// <summary>One row of <c>/fapi/v1/ticker/24hr</c> with no symbol — the single most expensive call
/// the adapter makes (weight 40, measured), and it is made for exactly one field: <c>quoteVolume</c>,
/// the rolling 24 h turnover in the quote asset. <c>lastPrice</c> comes along and is used as the
/// snapshot's last traded price, which no cheaper batched endpoint carries.</summary>
internal sealed record BinanceTicker24h
{
    public string Symbol { get; init; } = "";
    public string LastPrice { get; init; } = "0";
    public string QuoteVolume { get; init; } = "0";
}

/// <summary>Response of <c>/fapi/v1/openInterest?symbol=..</c> (weight 1). There is no batched form:
/// omitting <c>symbol</c> is HTTP 400 <c>-1102</c>, verified live. Hence
/// <see cref="BinanceOpenInterestFeed"/>.</summary>
internal sealed record BinanceOpenInterest
{
    public string Symbol { get; init; } = "";
    public string OpenInterest { get; init; } = "0";

    /// <summary>The venue's own sampling instant for this number — carried through to
    /// <c>open_interest_at</c>, which exists because OI is a separate, slower call.</summary>
    public long Time { get; init; }
}

/// <summary>One row of <c>/fapi/v1/fundingRate</c> — historical funding payments, oldest first,
/// windowed by startTime/endTime. Weight-free like fundingInfo.</summary>
internal sealed record BinanceFundingRateRow
{
    public long FundingTime { get; init; }
    public string FundingRate { get; init; } = "0";
}

/// <summary>
/// Response of <c>/fapi/v1/depth?symbol=..&amp;limit=..</c>. Levels are <c>[price, qty]</c> pairs of
/// strings. <c>lastUpdateId</c> is what seeds a WebSocket book (see <see cref="BinanceBookBuilder"/>);
/// <c>E</c> and <c>T</c> are the venue's event and transaction clocks in ms.
/// </summary>
internal sealed record BinanceDepth
{
    public long LastUpdateId { get; init; }

    /// <summary>Event time, ms. Explicitly named because the camelCase policy would look for "e",
    /// and "e" is the EVENT TYPE on the WebSocket frames of the very same book — the collision this
    /// file's options object exists to keep impossible.</summary>
    [JsonPropertyName("E")] public long EventTime { get; init; }

    public List<string[]>? Bids { get; init; }
    public List<string[]>? Asks { get; init; }
}

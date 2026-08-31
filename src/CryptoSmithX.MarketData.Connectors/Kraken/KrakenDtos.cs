using System.Text.Json.Serialization;

namespace CryptoSmithX.MarketData.Connectors.Kraken;

// The JSON shapes of the Kraken Futures public REST responses the adapter reads. Kraken names its
// fields in camelCase; these bind to them case-insensitively (JsonSerializerDefaults.Web). Only the
// fields the adapter maps are declared — everything else in the payloads is ignored on the way in.
// Property-init records so the deserialiser can construct them and the client can capture the raw
// element text with a `with`-expression.

internal sealed record KrakenInstrument
{
    public string Symbol { get; init; } = "";
    public string Type { get; init; } = "";
    public string Quote { get; init; } = "";
    public decimal TickSize { get; init; }
    public decimal ContractSize { get; init; }
    public int ContractValueTradePrecision { get; init; }
    public DateTimeOffset? OpeningDate { get; init; }
    public bool Tradeable { get; init; }
    public bool PostOnly { get; init; }
    public bool IsExpired { get; init; }

    /// <summary>The venue's payload for this instrument, captured verbatim by the client (not bound
    /// from a field). Stored on <c>exchange_instrument.raw_json</c>.</summary>
    [JsonIgnore]
    public string RawJson { get; init; } = "";
}

internal sealed record KrakenTickersResponse
{
    public DateTimeOffset ServerTime { get; init; }
    public List<KrakenTicker> Tickers { get; init; } = [];
}

internal sealed record KrakenTicker
{
    public string Symbol { get; init; } = "";
    public double Last { get; init; }
    public double Bid { get; init; }
    public double Ask { get; init; }
    public double BidSize { get; init; }
    public double AskSize { get; init; }
    public double MarkPrice { get; init; }
    public double IndexPrice { get; init; }

    /// <summary>Absolute rate (quote per contract). The adapter divides by mark to get the fraction.</summary>
    public double FundingRate { get; init; }

    /// <summary>Rolling 24 h turnover in the quote asset.</summary>
    public double VolumeQuote { get; init; }

    /// <summary>Open interest in units of the base asset.</summary>
    public double OpenInterest { get; init; }
}

internal sealed record KrakenCandlesResponse
{
    public List<KrakenCandle> Candles { get; init; } = [];
}

// open/high/low/close/volume arrive as strings; time is the bar-open time in unix milliseconds.
internal sealed record KrakenCandle
{
    public long Time { get; init; }
    public string Open { get; init; } = "0";
    public string High { get; init; } = "0";
    public string Low { get; init; } = "0";
    public string Close { get; init; } = "0";
    public string Volume { get; init; } = "0";
}

internal sealed record KrakenFundingResponse
{
    public List<KrakenFundingRate> Rates { get; init; } = [];
}

internal sealed record KrakenFundingRate
{
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Fraction of notional for the interval — used as-is; Kraken already relativised it.</summary>
    public double RelativeFundingRate { get; init; }
}

internal sealed record KrakenOrderBookResponse
{
    public DateTimeOffset ServerTime { get; init; }
    public KrakenOrderBook OrderBook { get; init; } = new();
}

// Each level is [price, size]. Bids come back ascending (best/highest last), asks ascending
// (best/lowest first); the adapter finds best-of-side by min/max rather than trusting the order.
internal sealed record KrakenOrderBook
{
    public double[][] Bids { get; init; } = [];
    public double[][] Asks { get; init; } = [];
}

namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// What one venue's own socket is holding for one instrument right now — the live path's only
/// currency, and deliberately NOT a <see cref="Ticker"/>.
///
/// A <see cref="Ticker"/> is a row: it is written whole or not at all, so every figure on it is
/// required and the adapter fills the gaps from REST to keep that promise. This record is the
/// opposite promise. Every field is nullable because <c>null</c> here means "this venue's socket
/// does not carry this", which is a fact about the venue and is answered by leaving the page's
/// figure on the database rather than by asking REST. WEEX publishes no ticker on its socket at
/// all; Binance's carries mark, index and funding but no open interest. Filling either from REST
/// would put a per-instrument HTTP call on a 200 ms tick, which is the one thing the live path
/// must never do.
/// </summary>
/// <param name="At">
/// When the FEED received this, read from the cache entry that holds it — never "now". The whole
/// live path exists to answer "how old is this figure", and stamping a poll with the poll's own
/// clock would make a frozen socket read as perpetually fresh.
/// </param>
public sealed record LiveQuote(
    string ExchangeSymbol,
    DateTimeOffset At,
    double? BidPrice,
    double? BidSize,
    double? AskPrice,
    double? AskSize,
    double? LastPrice,
    double? MarkPrice,
    double? IndexPrice,
    double? FundingRate,
    double? OpenInterest,
    double? Turnover24h,
    Depth? Depth);

using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors;

/// <summary>
/// Everything the hub needs from a venue. Public endpoints only — this service holds no keys and
/// places no orders. Implementations translate the wire format into the canonical records and
/// normalise units to what the DDL documents.
/// </summary>
public interface IExchangeMarketData
{
    /// <summary>Matches <c>exchange.code</c>.</summary>
    string SegmentCode { get; }

    /// <summary>
    /// Which <c>dataset.code</c>s this adapter honestly implements, and over which transport(s) —
    /// a fixed fact about the adapter instance, not something that changes at runtime. A dataset
    /// absent from this list gets <c>we_implement=false</c> when <c>ExchangeWorker</c> declares
    /// capability into <c>segment_dataset_capability</c> (0014); the fake's depth is the standing
    /// example: <see cref="GetOrderBookAsync"/> always returns null, so it declares no depth entry
    /// here even though a <c>DepthCollector</c> could technically be pointed at it.
    /// </summary>
    IReadOnlyList<DatasetCapability> Capabilities { get; }

    /// <summary>Linear perpetuals with a USD-family quote. Dated and inverse contracts are skipped.</summary>
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct);

    /// <summary>Every instrument in one call where the venue allows it.</summary>
    Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct);

    /// <summary>Closed 1-minute bars only; a bar still forming is never returned.</summary>
    Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    /// <summary>
    /// Historical funding payments in [from, to], oldest first. Venues serve these back in time,
    /// so the Hub can back-fill the series rather than only recording the live rate.
    /// </summary>
    Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    /// <summary>
    /// The order book for one instrument, reduced to the cumulative-notional bands the snapshot
    /// stores, or null when the venue carries the book inline in its ticker and has no separate call
    /// (the fake). A per-symbol call: the depth collector paces it and asks only for trading
    /// instruments, so it lives apart from <see cref="GetTickersAsync"/>.
    /// </summary>
    Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct);

    // ---------------------------------------------------------------------------------------
    // The datasets whose tables 0032 created. Default implementations rather than required
    // members, and that is the honest shape: "this venue publishes no such thing" is a fact about
    // the venue, not a gap in the adapter, and every one of these is genuinely absent somewhere.
    // A venue that does publish one overrides it AND declares the dataset in Capabilities — the
    // two together are what let ExchangeWorker start a loop for it.
    // ---------------------------------------------------------------------------------------

    /// <summary>Every trade received from the live socket since the last call, oldest first, and
    /// removed from the buffer by the call. Drained rather than fetched because trades are a push
    /// stream: there is no "current trade" to poll, which is the whole reason
    /// <see cref="Streaming.EventBuffer{T}"/> exists.</summary>
    IReadOnlyList<TradeEvent> DrainTrades() => [];

    /// <summary>
    /// Liquidation events received since the last call, KEPT APART from <see cref="DrainTrades"/>
    /// on purpose. Binance publishes liquidations on their own stream (<c>!forceOrder@arr</c>) AND
    /// their fills again on the ordinary tape (<c>@aggTrade</c>) — storing both as <c>trade</c> rows
    /// would count the same executed quantity twice. These are bucketed into
    /// <c>liquidation_volume_history</c> instead, which is the table for exactly this number.
    ///
    /// Kraken needs nothing here: its tape marks a liquidation inline as a trade type, so its
    /// liquidations are already in <see cref="DrainTrades"/> without any double counting, and its
    /// bucketed volume comes from the venue's own analytics series.
    /// </summary>
    IReadOnlyList<TradeEvent> DrainLiquidations() => [];

    /// <summary>
    /// Only what this venue's own socket holds, for the <paramref name="exchangeSymbols"/> asked
    /// about and no others, and only while younger than <paramref name="maxAge"/>. Never REST — an
    /// adapter with no socket returns nothing, and that is the answer, not a gap to be filled.
    ///
    /// This is the live path's only read, and it is a POLL of caches the feeds already keep rather
    /// than a subscription, because <see cref="Streaming.MarketCache{T}"/> raises no events. That
    /// is what makes it free: conflation falls out of the cache being last-write-wins, and not one
    /// line changes inside the feeds.
    ///
    /// <b>Why the caller names the symbols.</b> It answered for the whole venue first, and the tick
    /// could not hold: assembling a quote means reading the book, and reading a book means summing
    /// cumulative notional across its levels. Measured on the test host at one instrument per venue,
    /// whole-venue polling cost ~285 ms a tick on Kraken's 275 symbols alone and collapsed a
    /// four-venue page to one frame per ten seconds — against a 200 ms budget the plan calls
    /// non-negotiable. A page watches a handful of listings; this now costs a handful of lookups.
    ///
    /// <paramref name="maxAge"/> is applied where a seam takes it; where a feed's own try-get
    /// already gates on staleness it is that gate that applies, configured from the same
    /// <c>ws_stale_after_s</c> setting the caller reads this argument from. A feed that answers
    /// "no fresh sample" and an instrument absent from the result are the same statement.
    /// </summary>
    IReadOnlyList<LiveQuote> LiveQuotes(IReadOnlyCollection<string> exchangeSymbols, TimeSpan maxAge) => [];

    /// <summary>The top <paramref name="levels"/> of the maintained book right now, or false when
    /// this venue keeps no raw book (the fake) or has not seeded this symbol yet.</summary>
    bool TryGetBookFrame(string exchangeSymbol, int levels, out BookFrame frame)
    {
        frame = null!;
        return false;
    }

    /// <summary>Open-interest history in [from, to]. Empty where the venue publishes no history
    /// series — WEEX and Hyperliquid both answer only "OI right now", and the Hub buckets that
    /// itself rather than inventing a series the venue never served.</summary>
    Task<IReadOnlyList<OpenInterestBucket>> GetOpenInterestHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OpenInterestBucket>>([]);

    /// <summary>Closed 1-minute mark- or index-price bars in [from, to]. Only Binance publishes
    /// either; the other three have no such endpoint on any transport.</summary>
    Task<IReadOnlyList<PriceCandle>> GetPriceCandles1mAsync(
        string exchangeSymbol, string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PriceCandle>>([]);

    /// <summary>Aggregated liquidation volume per bucket in [from, to], from the venue's own
    /// analytics. Only Kraken publishes one; Binance's liquidations arrive as individual socket
    /// events and land in <c>trade</c> with trade_type='liquidation' instead.</summary>
    Task<IReadOnlyList<LiquidationBucket>> GetLiquidationVolumeAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LiquidationBucket>>([]);
}

/// <summary>One declared capability: this adapter implements <paramref name="DatasetCode"/>, using
/// <paramref name="TransportsUs"/> (comma-joined, e.g. "rest" or "rest,ws" — matches the <c>list</c>
/// kind of <c>capability_key</c> in 0014).</summary>
public sealed record DatasetCapability(string DatasetCode, string TransportsUs);

using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Gate;

/// <summary>
/// Gate's USDT-settled perpetuals as an <see cref="IExchangeMarketData"/>.
///
/// <b>Why this venue is cheap.</b> One <c>futures/usdt/tickers</c> closes the snapshot across 981
/// contracts — 461 KB measured, every column filled.
///
/// <b>The one thing that makes this venue different from its two neighbours: it quotes in
/// CONTRACTS.</b> <c>total_size</c>, <c>volume_24h</c>, <c>highest_size</c> and <c>lowest_size</c>
/// are counts of contracts, and one contract is <c>quanto_multiplier</c> of the base asset —
/// 0.0001 BTC on BTC_USDT. Those figures are stored RAW and the multiplier travels on the
/// instrument, exactly as Kraken's contract size already does; the studio multiplies at render and
/// shows the venue's own untouched figure in the title. The venue proves the arithmetic itself:
/// 207 254 174 contracts × 0.0001 is 20 725, and its own <c>volume_24h_base</c> reads 20 725.
///
/// <b>Depth is not declared yet.</b> Top-of-book size comes off the ticker; the cumulative bands
/// need a maintained book, which here means the socket — <c>futures.order_book</c> gives a snapshot
/// whose <c>id</c> equals the <c>u</c> of the synchronous update stream, the same shape WEEX has,
/// except the chain rule is <c>U == prev.u + 1</c>, which is not written yet. Next slice.
/// </summary>
public sealed class GatePerpMarketData : IExchangeMarketData
{
    private readonly GateClient _client;

    public GatePerpMarketData(GateClient client) => _client = client;

    public string SegmentCode => "gate-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var contracts = await _client.GetContractsAsync(ct);

        var list = new List<Instrument>(contracts.Count);
        foreach (var c in contracts)
        {
            // Gate names the pair with an underscore — BTC_USDT — and that split is the venue's own
            // rather than a guess at where a concatenated symbol divides. A name without one is not
            // a perpetual of this shape and is skipped rather than guessed at.
            var cut = c.Name.LastIndexOf('_');
            if (cut <= 0 || cut == c.Name.Length - 1)
            {
                continue;
            }

            list.Add(new Instrument(
                ExchangeSymbol: c.Name,
                BaseAssetRaw: c.Name[..cut],
                QuoteAssetRaw: c.Name[(cut + 1)..],
                // THE ONE THAT MATTERS ON THIS VENUE — see the class remarks. Absent or zero would
                // make every size on the venue silently wrong by orders of magnitude, so it falls
                // back to 1 only when the venue itself published nothing, and that case is visible
                // as a multiplier of ×1 on a venue where nothing else has one.
                ContractMultiplier: Dec(c.QuantoMultiplier) is { } q && q > 0 ? q : 1m,
                PriceStep: Dec(c.OrderPriceRound),
                // The venue states a minimum in contracts and no separate increment; one contract is
                // the increment by construction.
                QtyStep: 1m,
                MinQty: c.OrderSizeMin is { } min && min > 0 ? min : null,
                // Gate publishes no minimum notional on this route.
                MinNotional: null,
                // SECONDS on this venue — a third unit for the same concept across three venues.
                FundingIntervalHours: c.FundingInterval is { } s && s > 0
                    ? (short)Math.Max(1, s / 3600)
                    : null,
                ListedAt: c.CreateTime is { } t && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
                // The venue's own flag, and the only lifecycle signal on this route. A contract it
                // has marked for delisting is still listed until it is gone, which is what Halted
                // says; Delisted would claim an event that has not happened yet.
                Status: c.InDelisting ? InstrumentStatus.Halted : InstrumentStatus.Trading,
                RawJson: JsonSerializer.Serialize(c)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        var rows = await _client.GetTickersAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var list = new List<Ticker>(rows.Count);
        foreach (var t in rows)
        {
            list.Add(new Ticker(
                ExchangeSymbol: t.Contract,
                ReceivedAt: now,
                LastPrice: Num(t.Last),
                BidPrice: Num(t.HighestBid),
                AskPrice: Num(t.LowestAsk),
                // CONTRACTS — the instrument's multiplier is what turns these into base units, and
                // it is applied downstream rather than here, so the row keeps the venue's own figure.
                BidSize: Num(t.HighestSize),
                AskSize: Num(t.LowestSize),
                MarkPrice: Num(t.MarkPrice),
                IndexPrice: Num(t.IndexPrice),
                FundingRate: Num(t.FundingRate),
                Turnover24h: Num(t.Volume24hQuote),
                // Contracts again, same reasoning as the sizes above.
                OpenInterest: Num(t.TotalSize),
                OpenInterestAt: now,
                Depth: null,
                // The venue publishes base volume ready-made beside the contract count, so this one
                // column does NOT go through the multiplier — it is already the venue's own answer.
                Volume24hBase: Num(t.Volume24hBase)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetCandles1mAsync(exchangeSymbol, from, to, ct);

        var list = new List<Candle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.T <= 0)
            {
                continue;
            }

            // SECONDS here, milliseconds on the other two venues in this batch.
            var openTime = DateTimeOffset.FromUnixTimeSeconds(c.T);
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Req(c.O),
                High: Req(c.H),
                Low: Req(c.L),
                Close: Req(c.C),
                // Contracts, stored raw like every other size on this venue.
                Volume: c.V ?? 0,
                TradeCount: null,
                VolumeQuote: Num(c.Sum)));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetFundingHistoryAsync(exchangeSymbol, ct);

        var list = new List<FundingRate>(rows.Count);
        foreach (var f in rows)
        {
            if (f.T <= 0 || Num(f.R) is not { } rate)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeSeconds(f.T);

            // The route takes no window, so it is applied here rather than handing the caller rows
            // it did not ask for.
            if (at < from || at > to)
            {
                continue;
            }

            list.Add(new FundingRate(exchangeSymbol, at, rate));
        }

        return list;
    }

    /// <summary>Null until the socket is wired — see the class remarks.</summary>
    public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct) =>
        Task.FromResult<Depth?>(null);

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"Gate sent a bar with an unreadable figure: '{s}'");

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

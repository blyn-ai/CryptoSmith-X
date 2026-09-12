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
/// <b>Depth, the tape, open interest and liquidations are all REST here.</b> The book is one call
/// per collected symbol on the depth sweep; the tape rides with it; and <c>contract_stats</c> gives
/// the venue's own hourly open-interest series AND the liquidated size on each side in the same
/// response — which is why this is the only venue of the six with a real open-interest history and
/// a liquidation series both.
///
/// The depth bands are the one place this venue's contracts must be converted BEFORE storage rather
/// than after: a band is a notional, computed here and stored finished. See
/// <see cref="ContractBook"/>.
/// </summary>
public sealed class GatePerpMarketData : IExchangeMarketData
{
    private readonly GateClient _client;
    private readonly RestTape _tape = new();

    /// <summary>Contract size per symbol, learned on the discovery pass. The depth bands need it at
    /// the moment they are computed, and this is the only place the adapter has it.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _multiplier =
        new(StringComparer.Ordinal);

    public GatePerpMarketData(GateClient client) => _client = client;

    public string SegmentCode => "gate-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("candles_mark", "rest"),
        new("candles_index", "rest"),
        // Both from contract_stats — see the class remarks.
        new("open_interest", "rest"),
        new("liquidations", "rest"),
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

            var multiplier = Dec(c.QuantoMultiplier) is { } q && q > 0 ? q : 1m;
            _multiplier[c.Name] = (double)multiplier;

            list.Add(new Instrument(
                ExchangeSymbol: c.Name,
                BaseAssetRaw: c.Name[..cut],
                QuoteAssetRaw: c.Name[(cut + 1)..],
                // THE ONE THAT MATTERS ON THIS VENUE — see the class remarks. Absent or zero would
                // make every size on the venue silently wrong by orders of magnitude, so it falls
                // back to 1 only when the venue itself published nothing, and that case is visible
                // as a multiplier of ×1 on a venue where nothing else has one.
                ContractMultiplier: multiplier,
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

    /// <summary>The book, and the tape with it. The levels are CONTRACTS, so the band is formed
    /// through the contract size — the conversion that has to happen here rather than downstream,
    /// because a band is already a notional by the time it is stored.</summary>
    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var book = await _client.GetOrderBookAsync(exchangeSymbol, ct);

        try
        {
            Fold(exchangeSymbol, await _client.GetTradesAsync(exchangeSymbol, ct));
        }
        catch (HttpRequestException)
        {
        }

        if (!_multiplier.TryGetValue(exchangeSymbol, out var multiplier))
        {
            // Discovery has not run for this symbol yet. Null rather than a band computed against an
            // assumed contract size — off by ten thousand on this venue, and indistinguishable from
            // a real figure once stored.
            return null;
        }

        return ContractBook.Compute(
            Levels(book.Bids), Levels(book.Asks), multiplier,
            at: book.Current is { } sec && sec > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(sec * 1000))
                : DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    /// <summary>
    /// Mark or index bars. This venue serves them from the SAME candle route as the traded ones,
    /// selected by a prefix on the contract name — <c>mark_BTC_USDT</c>, <c>index_BTC_USDT</c> —
    /// which is why there is no second client method for them.
    ///
    /// The bars carry <c>v</c> and <c>sum</c> of zero, correctly: neither series trades.
    /// </summary>
    public async Task<IReadOnlyList<PriceCandle>> GetPriceCandles1mAsync(
        string exchangeSymbol, string series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (series is not ("mark" or "index"))
        {
            return [];
        }

        var rows = await _client.GetCandles1mAsync($"{series}_{exchangeSymbol}", from, to, ct);

        var list = new List<PriceCandle>(rows.Count);
        foreach (var c in rows)
        {
            if (c.T <= 0)
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeSeconds(c.T);
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new PriceCandle(
                exchangeSymbol, series, openTime, Req(c.O), Req(c.H), Req(c.L), Req(c.C)));
        }

        return list;
    }

    /// <summary>
    /// The venue's own hourly open-interest series, in CONTRACTS as the venue states it — the same
    /// unit the snapshot's own open-interest column holds, so the two are the same measurement at
    /// two cadences rather than two numbers.
    /// </summary>
    public async Task<IReadOnlyList<OpenInterestBucket>> GetOpenInterestHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // The one grain <c>contract_stats</c> serves, stamped on every bucket this returns.
        const int intervalSeconds = 3600;

        var rows = await _client.GetContractStatsAsync(exchangeSymbol, from, limit: 100, ct);

        var list = new List<OpenInterestBucket>(rows.Count);
        foreach (var r in rows)
        {
            if (r.Time <= 0 || r.OpenInterest is not { } oi)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeSeconds(r.Time);
            if (at < from || at > to)
            {
                continue;
            }

            list.Add(new OpenInterestBucket(
                exchangeSymbol, intervalSeconds, at,
                Open: null, High: null, Low: null, Close: oi,
                // No quote figure on this route; never our own oi × mark.
                Quote: null,
                Source: "analytics"));
        }

        return list;
    }

    /// <summary>
    /// Liquidated size per hour, both sides summed — from the same <c>contract_stats</c> response the
    /// open-interest series comes from, so the two can never disagree about a period.
    ///
    /// The unit is CONTRACTS, and it is stated on the row rather than converted: the column's own
    /// schema keeps the venue's unit precisely because venues count this differently.
    /// </summary>
    public async Task<IReadOnlyList<LiquidationBucket>> GetLiquidationVolumeAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetContractStatsAsync(exchangeSymbol, from, limit: 100, ct);

        var list = new List<LiquidationBucket>(rows.Count);
        foreach (var r in rows)
        {
            if (r.Time <= 0)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeSeconds(r.Time);
            if (at < from || at > to)
            {
                continue;
            }

            var size = (r.LongLiqSize ?? 0) + (r.ShortLiqSize ?? 0);
            list.Add(new LiquidationBucket(exchangeSymbol, 3600, at, size, "base"));
        }

        return list;
    }

    private void Fold(string exchangeSymbol, IReadOnlyList<GateTrade> trades)
    {
        var events = new List<TradeEvent>(trades.Count);
        foreach (var t in trades)
        {
            if (Num(t.Price) is not { } price || price <= 0
                || t.Size is not { } signed
                || t.CreateTimeMs is not { } ms || ms <= 0)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

            events.Add(new TradeEvent(
                exchangeSymbol, at,
                t.Id.ToString(CultureInfo.InvariantCulture), t.Id,
                price,
                // The magnitude is the quantity and the SIGN is the side — see GateTrade.Size.
                Math.Abs(signed),
                signed >= 0 ? "buy" : "sell",
                null));

            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    private static List<(double Price, double Qty)> Levels(IReadOnlyList<GateBookLevel>? raw)
    {
        var levels = new List<(double Price, double Qty)>(raw?.Count ?? 0);
        foreach (var l in raw ?? [])
        {
            if (Num(l.P) is { } px && px > 0 && l.S > 0)
            {
                levels.Add((px, l.S));
            }
        }

        return levels;
    }

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"Gate sent a bar with an unreadable figure: '{s}'");

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

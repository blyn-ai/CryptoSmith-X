using System.Globalization;
using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Okx;

/// <summary>
/// OKX's perpetual swaps as an <see cref="IExchangeMarketData"/>.
///
/// <b>Five bulk calls, no per-symbol sweep.</b> Unlike the three venues landed before it, OKX
/// spreads the snapshot across separate routes — ticker, mark, open interest, funding and index —
/// but every one of them answers for the WHOLE segment at once. That matters more here than
/// elsewhere: this venue's rate limiter is keyed by endpoint × instId (reproduced — see
/// <see cref="OkxClient"/>), so a per-symbol design would hit a ceiling the bulk one never
/// approaches.
///
/// <b>Sizes are CONTRACTS, like Gate.</b> One contract is <c>ctVal</c> of the base asset — 0.01 BTC
/// on BTC-USDT-SWAP — and the venue proves it in its own two fields: <c>vol24h</c> 2 516 624.7
/// contracts × 0.01 is 25 166.247, which is <c>volCcy24h</c> exactly. Stored raw with the multiplier
/// on the instrument, the same way Kraken's contract size and Gate's quanto multiplier already are.
///
/// <b>The index is keyed by the PAIR, not the instrument.</b> <c>index-tickers</c> publishes
/// "BTC-USDT" while the swap is "BTC-USDT-SWAP", and the join is the venue's own <c>instFamily</c>.
/// One quote currency per call, so a swap settled in something else has no index row and its index
/// stays absent — borrowed from another currency it would be a different measurement.
///
/// <b>Depth, the tape and liquidations are REST here.</b> The book route is practically free on this
/// venue and measured so: 200 requests to the same instId all returned 200 in 2.49 s, because that
/// route's limit is keyed by UserID and we have no user. Liquidations are keyed by UNDERLYING rather
/// than instrument, so the adapter asks per <c>instFamily</c>.
///
/// The bands are formed through the contract size BEFORE storage — a band is a notional, computed
/// here and stored finished, while every other size on this venue is stored raw and multiplied
/// downstream. See <see cref="ContractBook"/>.
/// </summary>
public sealed class OkxPerpMarketData : IExchangeMarketData
{
    /// <summary>The quote currencies whose index series this adapter joins. Named rather than
    /// derived: each is one more bulk call, and the venue publishes the index per quote currency.
    /// A swap outside this set keeps a null index, which is honest — see the class remarks.</summary>
    private static readonly string[] IndexQuotes = ["USDT", "USDC", "USD"];

    private readonly OkxClient _client;
    private readonly RestTape _tape = new();

    /// <summary>Contract size and underlying per instrument, learned on discovery: the bands need the
    /// first at the moment they are computed, and the liquidation route takes the second as its key.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Multiplier, string Family)>
        _spec = new(StringComparer.Ordinal);

    public OkxPerpMarketData(OkxClient client) => _client = client;

    public string SegmentCode => "okx-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        new("liquidations", "rest"),
        // open_interest is absent: this venue's only public series is rubik's per-CURRENCY
        // aggregate, which is every contract on that coin added together and not this instrument's
        // own figure. The current value still reaches the snapshot every pass.
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var instruments = await _client.GetInstrumentsAsync(ct);
        var funding = await _client.GetFundingRatesAsync(ct);
        var fundingById = funding
            .GroupBy(f => f.InstId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var list = new List<Instrument>(instruments.Count);
        foreach (var i in instruments)
        {
            // Linear only: an inverse swap is margined in the base asset and its sizes mean a
            // different thing, which is a second segment rather than a row in this one.
            if (!string.Equals(i.CtType, "linear", StringComparison.Ordinal))
            {
                continue;
            }

            // baseCcy is EMPTY on a swap — instFamily is where the venue states the pair.
            if (i.InstFamily is not { Length: > 0 } family)
            {
                continue;
            }

            var cut = family.IndexOf('-');
            if (cut <= 0 || cut == family.Length - 1)
            {
                continue;
            }

            var multiplier = Dec(i.CtVal) is { } v && v > 0 ? v : 1m;
            _spec[i.InstId] = ((double)multiplier, family);

            list.Add(new Instrument(
                ExchangeSymbol: i.InstId,
                BaseAssetRaw: family[..cut],
                QuoteAssetRaw: family[(cut + 1)..],
                // See the class remarks: every size on this venue is a count of contracts.
                ContractMultiplier: multiplier,
                PriceStep: Dec(i.TickSz),
                QtyStep: Dec(i.LotSz),
                MinQty: Dec(i.MinSz),
                // OKX states no minimum notional on this route.
                MinNotional: null,
                // THE VENUE'S OWN ARITHMETIC, not ours: the gap between this period's settlement and
                // the next one is the interval, and both instants come from the same row.
                FundingIntervalHours: IntervalHours(fundingById.GetValueOrDefault(i.InstId)),
                ListedAt: Instant(i.ListTime),
                Status: i.State switch
                {
                    "live" => InstrumentStatus.Trading,
                    "suspend" or "preopen" or "test" => InstrumentStatus.Halted,
                    "expired" => InstrumentStatus.Delisted,
                    _ => InstrumentStatus.Halted,
                },
                RawJson: JsonSerializer.Serialize(i)));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        // Five independent reads of the same segment, issued together: they are separate routes on
        // the venue's side but one observation on ours, and serialising them would spread the
        // snapshot's own instant across five round trips.
        var tickersTask = _client.GetTickersAsync(ct);
        var marksTask = _client.GetMarkPricesAsync(ct);
        var oiTask = _client.GetOpenInterestAsync(ct);
        var fundingTask = _client.GetFundingRatesAsync(ct);
        var instrumentsTask = _client.GetInstrumentsAsync(ct);
        await Task.WhenAll(tickersTask, marksTask, oiTask, fundingTask, instrumentsTask);

        var marks = marksTask.Result.ToDictionary(m => m.InstId, StringComparer.Ordinal);
        var oi = oiTask.Result.ToDictionary(o => o.InstId, StringComparer.Ordinal);
        var funding = fundingTask.Result
            .GroupBy(f => f.InstId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // instId -> instFamily, which is the key the index series is published under.
        var familyOf = instrumentsTask.Result
            .Where(i => i.InstFamily is { Length: > 0 })
            .ToDictionary(i => i.InstId, i => i.InstFamily!, StringComparer.Ordinal);

        var index = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var quote in IndexQuotes)
        {
            foreach (var row in await _client.GetIndexTickersAsync(quote, ct))
            {
                if (Num(row.IdxPx) is { } px)
                {
                    index[row.InstId] = px;
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        var list = new List<Ticker>(tickersTask.Result.Count);
        foreach (var t in tickersTask.Result)
        {
            oi.TryGetValue(t.InstId, out var openInterest);
            funding.TryGetValue(t.InstId, out var fund);

            double? idx = familyOf.TryGetValue(t.InstId, out var family)
                          && index.TryGetValue(family, out var px)
                ? px
                : null;

            list.Add(new Ticker(
                ExchangeSymbol: t.InstId,
                ReceivedAt: now,
                LastPrice: Num(t.Last),
                BidPrice: Num(t.BidPx),
                AskPrice: Num(t.AskPx),
                // Contracts — the instrument's multiplier is what turns these into base units.
                BidSize: Num(t.BidSz),
                AskSize: Num(t.AskSz),
                MarkPrice: marks.TryGetValue(t.InstId, out var mark) ? Num(mark.MarkPx) : null,
                IndexPrice: idx,
                FundingRate: Num(fund?.FundingRate),
                // The venue publishes no 24-hour quote turnover on this route; volCcy24h is the
                // BASE volume and goes to its own column rather than standing in for turnover.
                Turnover24h: null,
                OpenInterest: Num(openInterest?.Oi),
                OpenInterestAt: Instant(openInterest?.Ts) ?? now,
                Depth: null,
                // The ticker frame's own clock.
                VenueTs: Instant(t.Ts),
                NextFundingAt: Instant(fund?.FundingTime),
                Volume24hBase: Num(t.VolCcy24h),
                // Computed by the venue itself, so it is written as published.
                OiQuote: Num(openInterest?.OiUsd)));
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
            // [ts, open, high, low, close, vol(contracts), volCcy(base), volCcyQuote, confirm]
            if (c.Length < 7 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: Req(c[1]),
                High: Req(c[2]),
                Low: Req(c[3]),
                Close: Req(c[4]),
                // Contracts, stored raw like every other size on this venue.
                Volume: Req(c[5]),
                TradeCount: null,
                VolumeQuote: c.Length > 7 ? Num(c[7]) : null));
        }

        return list;
    }

    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetFundingHistoryAsync(exchangeSymbol, from, to, ct);

        var list = new List<FundingRate>(rows.Count);
        foreach (var f in rows)
        {
            if (Num(f.FundingRate) is not { } rate || Instant(f.FundingTime) is not { } at)
            {
                continue;
            }

            list.Add(new FundingRate(exchangeSymbol, at, rate));
        }

        return list;
    }

    /// <summary>The book, and the tape with it.</summary>
    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var pages = await _client.GetBookAsync(exchangeSymbol, ct);
        var book = pages.FirstOrDefault();
        if (book is null)
        {
            return null;
        }

        try
        {
            Fold(exchangeSymbol, await _client.GetTradesAsync(exchangeSymbol, ct));
        }
        catch (HttpRequestException)
        {
        }

        if (!_spec.TryGetValue(exchangeSymbol, out var spec))
        {
            // Discovery has not reached this instrument. Null rather than a band against an assumed
            // contract size — a hundredfold error on this venue, invisible once stored.
            return null;
        }

        return ContractBook.Compute(
            Levels(book.Bids), Levels(book.Asks), spec.Multiplier,
            at: Instant(book.Ts) ?? DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    /// <summary>
    /// Filled liquidations, bucketed hourly.
    ///
    /// The route is keyed by UNDERLYING, so one call covers every contract on that family and the
    /// response is filtered back down to the instrument asked for. Sizes are CONTRACTS and the unit
    /// is stated on the row rather than converted — the column's schema keeps the venue's own unit
    /// precisely because venues count this differently.
    /// </summary>
    public async Task<IReadOnlyList<LiquidationBucket>> GetLiquidationVolumeAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!_spec.TryGetValue(exchangeSymbol, out var spec))
        {
            return [];
        }

        var groups = await _client.GetLiquidationsAsync(spec.Family, ct);

        var byHour = new Dictionary<long, double>();
        foreach (var g in groups)
        {
            // A family covers several contracts; only this one's events belong to this series.
            if (!string.Equals(g.InstId, exchangeSymbol, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var d in g.Details ?? [])
            {
                if (Instant(d.Ts) is not { } at || Num(d.Sz) is not { } sz || sz <= 0)
                {
                    continue;
                }

                if (at < from || at > to)
                {
                    continue;
                }

                var hour = at.ToUnixTimeSeconds() / 3600 * 3600;
                byHour[hour] = byHour.GetValueOrDefault(hour) + sz;
            }
        }

        return byHour
            .OrderBy(kv => kv.Key)
            .Select(kv => new LiquidationBucket(
                exchangeSymbol, 3600, DateTimeOffset.FromUnixTimeSeconds(kv.Key), kv.Value, "base"))
            .ToList();
    }

    private void Fold(string exchangeSymbol, IReadOnlyList<OkxTrade> trades)
    {
        var events = new List<TradeEvent>(trades.Count);
        foreach (var t in trades)
        {
            if (t.TradeId is not { Length: > 0 } id
                || Num(t.Px) is not { } price || price <= 0
                || Num(t.Sz) is not { } size
                || Instant(t.Ts) is not { } at)
            {
                continue;
            }

            events.Add(new TradeEvent(
                exchangeSymbol, at, id, null, price, size,
                string.Equals(t.Side, "buy", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell",
                null));

            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    /// <summary>[price, size, "0", orders] into levels — the two trailing fields are the venue's own
    /// bookkeeping and neither is a quantity.</summary>
    private static List<(double Price, double Qty)> Levels(IReadOnlyList<string[]>? raw)
    {
        var levels = new List<(double Price, double Qty)>(raw?.Count ?? 0);
        foreach (var l in raw ?? [])
        {
            if (l.Length >= 2 && Num(l[0]) is { } px && px > 0 && Num(l[1]) is { } qty && qty > 0)
            {
                levels.Add((px, qty));
            }
        }

        return levels;
    }

    /// <summary>The gap between the settlement now due and the one after it, as whole hours. The
    /// venue states both instants; this is its arithmetic, not a guess at its schedule.</summary>
    private static short? IntervalHours(OkxFundingRate? f)
    {
        if (Instant(f?.FundingTime) is not { } current || Instant(f?.NextFundingTime) is not { } next)
        {
            return null;
        }

        var hours = (next - current).TotalHours;
        return hours is > 0 and <= short.MaxValue ? (short)Math.Round(hours) : null;
    }

    private static double? Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : null;

    private static double Req(string? s) =>
        Num(s) ?? throw new InvalidOperationException($"OKX sent a bar with an unreadable figure: '{s}'");

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTimeOffset? Instant(string? ms) =>
        long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(v)
            : null;
}

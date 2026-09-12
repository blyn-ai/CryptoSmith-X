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
/// <b>Depth is not declared.</b> The book needs a socket, and this is the one venue in the queue
/// that needs TWO of them (<c>candle1m</c> answers 60018 on /public and only serves on /business) —
/// which is exactly why the roadmap moved it down two places. Next slice.
/// </summary>
public sealed class OkxPerpMarketData : IExchangeMarketData
{
    /// <summary>The quote currencies whose index series this adapter joins. Named rather than
    /// derived: each is one more bulk call, and the venue publishes the index per quote currency.
    /// A swap outside this set keeps a null index, which is honest — see the class remarks.</summary>
    private static readonly string[] IndexQuotes = ["USDT", "USDC", "USD"];

    private readonly OkxClient _client;

    public OkxPerpMarketData(OkxClient client) => _client = client;

    public string SegmentCode => "okx-perp";

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

            list.Add(new Instrument(
                ExchangeSymbol: i.InstId,
                BaseAssetRaw: family[..cut],
                QuoteAssetRaw: family[(cut + 1)..],
                // See the class remarks: every size on this venue is a count of contracts.
                ContractMultiplier: Dec(i.CtVal) is { } v && v > 0 ? v : 1m,
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

    /// <summary>Null until a socket is wired — and this venue needs two of them.</summary>
    public Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct) =>
        Task.FromResult<Depth?>(null);

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

using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Mexc;

/// <summary>
/// MEXC's perpetual contracts as an <see cref="IExchangeMarketData"/>.
///
/// <b>The largest catalogue in the queue and the most careful access to it.</b> 1 192 contracts,
/// and the whole market is two bulk calls. That is not only convenience here: this is the venue the
/// reconnaissance refused to push, because the documented rate produced 50 % refusals and there is
/// an unverified report of the host banning an IP outright. Every route this adapter reads answers
/// for the whole segment at once, so the snapshot costs three requests a minute against a measured
/// safe ceiling of about 2.5 per SECOND. See <see cref="MexcClient"/> for the 510-under-a-200
/// translation that lets the venue slow us down at all.
///
/// <b>Linear only.</b> Ten contracts settle in the base coin and are inverse; they are skipped on
/// discovery, because on those a contract is worth <c>contractSize</c> of the QUOTE and every
/// quantity this adapter reports would otherwise be out by a factor of the price.
///
/// <b>Sizes are CONTRACTS</b>, one being <c>contractSize</c> of the base asset — 0.0001 BTC on
/// BTC_USDT. Stored raw with the multiplier on the instrument, like Gate and OKX.
///
/// <b>Bid and ask have no SIZE on this venue's ticker.</b> <c>bid1</c> and <c>ask1</c> are prices
/// only, so those two columns stay absent rather than borrowing a number from somewhere it does not
/// mean the same thing.
///
/// <b>No open-interest history</b> — <c>contract/openInterest/{symbol}</c> answers 403 with an HTML
/// body, which is the host refusing the route rather than rate-limiting us; every other route
/// answers 200 from the same address in the same second. That dataset is not declared, and the
/// current figure still reaches the snapshot every pass.
/// </summary>
public sealed class MexcPerpMarketData : IExchangeMarketData
{
    private readonly MexcClient _client;
    private readonly RestTape _tape = new();

    /// <summary>Contract size per symbol, learned on discovery — the depth bands need it at the
    /// moment they are formed.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _multiplier =
        new(StringComparer.Ordinal);

    public MexcPerpMarketData(MexcClient client) => _client = client;

    public string SegmentCode => "mexc-perp";

    public IReadOnlyList<DatasetCapability> Capabilities { get; } =
    [
        new("discovery", "rest"),
        new("snapshot", "rest"),
        new("candles", "rest"),
        new("funding", "rest"),
        new("depth", "rest"),
        new("trades", "rest"),
        // open_interest and liquidations are absent because this venue publishes neither:
        // contract/openInterest/{symbol} is a 404, and its tape carries no liquidation flag.
        new("spec_versions", "rest"),
    ];

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var contracts = await _client.GetContractsAsync(ct);
        var funding = await _client.GetFundingRatesAsync(ct);
        var cycleBySymbol = funding
            .Where(f => f.Symbol is { Length: > 0 } && f.CollectCycle is > 0)
            .GroupBy(f => f.Symbol!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().CollectCycle!.Value, StringComparer.Ordinal);

        var list = new List<Instrument>(contracts.Count);
        foreach (var c in contracts)
        {
            // The venue's own statement that its API will not serve this symbol — 44 of 1 192 say
            // so. Kept out here rather than rediscovered by every per-symbol collector, one failure
            // at a time.
            if (c.ApiAllowed == false)
            {
                continue;
            }

            if (c.BaseCoin is not { Length: > 0 } b || c.QuoteCoin is not { Length: > 0 } q)
            {
                continue;
            }

            // INVERSE. Ten of the 1 192 settle in the base coin — BTC_USD and its nine siblings —
            // and on those a contract is worth contractSize of the QUOTE, not of the base. This
            // segment is linear perpetuals, as IExchangeMarketData says, and every other adapter
            // already keeps them out; this one did not, and the contract size was read as base units
            // regardless. BTC_USD's 100 became a hundred BITCOIN a contract, and the grid showed
            // 2.8 trillion dollars of depth at 50 bps on a venue whose whole book is a few million.
            // A number that wrong is worse than an empty column, because it reads as a measurement.
            if (string.Equals(c.SettleCoin, b, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var multiplier = c.ContractSize is { } size && size > 0 ? size : 1d;
            _multiplier[c.Symbol] = multiplier;

            list.Add(new Instrument(
                ExchangeSymbol: c.Symbol,
                BaseAssetRaw: b,
                QuoteAssetRaw: q,
                ContractMultiplier: (decimal)multiplier,
                PriceStep: c.PriceUnit is { } p && p > 0 ? (decimal)p : null,
                // In contracts, like every quantity here.
                QtyStep: c.VolUnit is { } v && v > 0 ? (decimal)v : null,
                MinQty: c.MinVol is { } m && m > 0 ? (decimal)m : null,
                // MEXC states no minimum notional on this route.
                MinNotional: null,
                // From the FUNDING route: contract/detail carries a fundingInterval field that is
                // null on every one of the 1 192 contracts, so the only place the venue states its
                // own cycle is beside the rate.
                FundingIntervalHours: cycleBySymbol.TryGetValue(c.Symbol, out var cycle)
                    ? (short)cycle
                    : null,
                ListedAt: c.CreateTime is { } t && t > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t) : null,
                // state 0 is the only value observed across all 1 192 contracts, so anything else is
                // a state this adapter has never seen and reports as halted rather than guessing.
                Status: c.State == 0 ? InstrumentStatus.Trading : InstrumentStatus.Halted,
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
                ExchangeSymbol: t.Symbol,
                ReceivedAt: now,
                // The instant of the newest print this venue's tape has handed us, which is what
                // the Last-trade column holds. It comes from the tape rather than the ticker
                // because none of these five publish a "time of last trade" on the ticker at all,
                // and that column was empty for every venue but Avantis while five tapes sat in
                // the same process already knowing the answer. As fresh as the tape's own pass,
                // never fresher, and null until that pass has run.
                LastTradeAt: _tape.Last(t.Symbol)?.At,
                LastPrice: Fin(t.LastPrice),
                BidPrice: Fin(t.Bid1),
                AskPrice: Fin(t.Ask1),
                // Absent, and that is what the venue publishes: bid1 and ask1 are prices, and this
                // ticker carries no size beside either of them.
                BidSize: null,
                AskSize: null,
                // 'fairPrice' is this venue's word for the mark.
                MarkPrice: Fin(t.FairPrice),
                IndexPrice: Fin(t.IndexPrice),
                FundingRate: Fin(t.FundingRate),
                Turnover24h: Fin(t.Amount24),
                // Contracts — the multiplier on the instrument is what makes it a quantity.
                OpenInterest: Fin(t.HoldVol),
                OpenInterestAt: now,
                Depth: null,
                VenueTs: t.Timestamp is { } ms && ms > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : null,
                // Contracts as well; the venue publishes no ready-made base volume here.
                Volume24hBase: null));
        }

        return list;
    }

    public async Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var k = await _client.GetCandles1mAsync(exchangeSymbol, from, to, ct);

        // COLUMN-ORIENTED: parallel arrays rather than a row per bar, which is why the length of
        // the shortest one is what bounds the walk — a short column would otherwise read off the
        // end of another and pair a price with the wrong minute.
        var time = k.Time;
        if (time is null || k.Open is null || k.High is null || k.Low is null || k.Close is null)
        {
            return [];
        }

        var n = new[] { time.Count, k.Open.Count, k.High.Count, k.Low.Count, k.Close.Count }.Min();

        var list = new List<Candle>(n);
        for (var i = 0; i < n; i++)
        {
            // SECONDS on this venue.
            var openTime = DateTimeOffset.FromUnixTimeSeconds(time[i]);
            if (openTime + TimeSpan.FromMinutes(1) > to)
            {
                continue;
            }

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: openTime,
                Open: k.Open[i],
                High: k.High[i],
                Low: k.Low[i],
                Close: k.Close[i],
                // Contracts, stored raw.
                Volume: k.Vol is { } vol && i < vol.Count ? vol[i] : 0,
                TradeCount: null,
                VolumeQuote: k.Amount is { } amt && i < amt.Count ? amt[i] : null));
        }

        return list;
    }

    /// <summary>
    /// Settled funding, walked back page by page until the window is covered.
    ///
    /// <b>This replaced a hack.</b> The first version of this adapter stated that the venue publishes
    /// no settled series and stored our own reading of the CURRENT rate, stamped at the next
    /// settlement — a number that would have been wrong the moment the rate moved before that
    /// settlement arrived. <c>contract/funding_rate/history</c> exists and is 1 618 payments deep on
    /// BTC_USDT, measured.
    ///
    /// Newest first, so the walk stops at the first page whose OLDEST row is already behind
    /// <paramref name="from"/> — there is nothing older worth another request.
    /// </summary>
    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        const int PageSize = 100;

        // A backstop, not a window: a catch-up over a long gap is bounded by 'from' above, and this
        // only stops a venue that answered with a page count we cannot walk from spending the whole
        // collector pass on one symbol.
        const int MaxPages = 20;

        var list = new List<FundingRate>();

        for (var page = 1; page <= MaxPages; page++)
        {
            var body = await _client.GetFundingHistoryAsync(exchangeSymbol, page, PageSize, ct);
            var rows = body.ResultList;
            if (rows is null || rows.Count == 0)
            {
                break;
            }

            var oldestOnPage = DateTimeOffset.MaxValue;

            foreach (var r in rows)
            {
                if (Fin(r.FundingRate) is not { } rate
                    || r.SettleTime is not { } ms || ms <= 0)
                {
                    continue;
                }

                var at = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                if (at < oldestOnPage)
                {
                    oldestOnPage = at;
                }

                if (at >= from && at <= to)
                {
                    list.Add(new FundingRate(exchangeSymbol, at, rate));
                }
            }

            // Newest first: once a page reaches back past the window, every later page is older
            // still. Also covers the last page, where the venue returns fewer rows than asked.
            if (oldestOnPage <= from || rows.Count < PageSize)
            {
                break;
            }

            if (body.TotalPage is { } total && page >= total)
            {
                break;
            }
        }

        return list;
    }

    /// <summary>The book, and the tape with it. Levels are CONTRACTS, so the band is formed through
    /// the contract size here rather than downstream.</summary>
    public async Task<Depth?> GetOrderBookAsync(string exchangeSymbol, CancellationToken ct)
    {
        var book = await _client.GetDepthAsync(exchangeSymbol, ct);

        try
        {
            Fold(exchangeSymbol, await _client.GetDealsAsync(exchangeSymbol, ct));
        }
        catch (HttpRequestException)
        {
            // Including a 510 translated into a rate limit: the book reading still stands, and the
            // gate has already been told to slow down by the exception itself.
        }

        if (!_multiplier.TryGetValue(exchangeSymbol, out var multiplier))
        {
            return null;
        }

        return ContractBook.Compute(
            Levels(book.Bids), Levels(book.Asks), multiplier,
            at: book.Timestamp is { } ms && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TradeEvent> DrainTrades() => _tape.Drain();

    private void Fold(string exchangeSymbol, IReadOnlyList<MexcDeal> deals)
    {
        var events = new List<TradeEvent>(deals.Count);
        foreach (var d in deals)
        {
            if (d.I is not { Length: > 0 } id
                || Fin(d.P) is not { } price || price <= 0
                || Fin(d.V) is not { } size
                || d.Time is not { } ms || ms <= 0)
            {
                continue;
            }

            var at = DateTimeOffset.FromUnixTimeMilliseconds(ms);

            events.Add(new TradeEvent(
                exchangeSymbol, at, id, null, price, size,
                // 1 is a buy and 2 a sell on this venue — a number standing for a side, mapped
                // explicitly so an unfamiliar third value reads as a sell rather than as a buy by
                // accident of truthiness.
                d.T == 1 ? "buy" : "sell",
                null));

            _tape.Saw(exchangeSymbol, price, at);
        }

        _tape.Observe(exchangeSymbol, events);
    }

    /// <summary>[price, volume, orders] into levels — the third field is an order count, not a
    /// quantity.</summary>
    private static List<(double Price, double Qty)> Levels(IReadOnlyList<double[]>? raw)
    {
        var levels = new List<(double Price, double Qty)>(raw?.Count ?? 0);
        foreach (var l in raw ?? [])
        {
            if (l.Length >= 2 && double.IsFinite(l[0]) && l[0] > 0 && double.IsFinite(l[1]) && l[1] > 0)
            {
                levels.Add((l[0], l[1]));
            }
        }

        return levels;
    }

    /// <summary>These arrive as JSON numbers rather than strings, so the only guard needed is
    /// against a venue sending a non-finite one into a column that means a price.</summary>
    private static double? Fin(double? v) => v is { } d && double.IsFinite(d) ? d : null;
}

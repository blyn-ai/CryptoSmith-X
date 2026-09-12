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
/// <b>Sizes are CONTRACTS</b>, one being <c>contractSize</c> of the base asset — 0.0001 BTC on
/// BTC_USDT. Stored raw with the multiplier on the instrument, like Gate and OKX.
///
/// <b>Bid and ask have no SIZE on this venue's ticker.</b> <c>bid1</c> and <c>ask1</c> are prices
/// only, so those two columns stay absent rather than borrowing a number from somewhere it does not
/// mean the same thing.
///
/// <b>No open-interest history</b> — <c>contract/openInterest/{symbol}</c> is a 404 — so that
/// dataset is not declared, and the current figure still reaches the snapshot every pass.
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
    /// The current rate for every symbol, as one observation each.
    ///
    /// This venue publishes no settled-funding series on any public route, so what the funding
    /// dataset holds here is our own reading of the current rate at the instant we asked — the same
    /// honest shape WEEX's open interest already has. The rate is stamped at the settlement the
    /// venue itself names, not at our clock, so two passes inside one period collide on the key
    /// rather than inventing two payments.
    /// </summary>
    public async Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(
        string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await _client.GetFundingRatesAsync(ct);

        foreach (var f in rows)
        {
            if (!string.Equals(f.Symbol, exchangeSymbol, StringComparison.Ordinal))
            {
                continue;
            }

            if (Fin(f.FundingRate) is not { } rate || f.NextSettleTime is not { } next || next <= 0)
            {
                return [];
            }

            var at = DateTimeOffset.FromUnixTimeMilliseconds(next);
            return at >= from && at <= to ? [new FundingRate(exchangeSymbol, at, rate)] : [];
        }

        return [];
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

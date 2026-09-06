using System.Net;
using CryptoSmithX.MarketData.Connectors.Binance;
using CryptoSmithX.MarketData.Connectors.Market;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Drives the Binance USDⓈ-M adapter against a stub handler replaying canonical payloads captured
/// from the live public API and trimmed to ten symbols — so the whole HTTP → JSON → mapping path
/// runs with no network. The ten were not chosen for tidiness; each one is a case the adapter has to
/// get right:
///
///   BTCUSDT, DOGEUSDT, 1000PEPEUSDT, BTCUSDC   in scope, and 1000PEPE pins the unnormalised raw base
///   ETHUSDT          present in every batch EXCEPT bookTicker — the honest-omission subject
///   XAUUSDT          contractType TRADIFI_PERPETUAL: undocumented, and an equity/metal perpetual
///   BTCUSDT_260925   contractType CURRENT_QUARTER: dated, out of V1 scope
///   XTZUSDT          fundingIntervalHours 4 — the venue has no single funding interval
///   OMGUSDT          status SETTLING
///   GAIBUSDT         status PENDING_TRADING
/// </summary>
public sealed class BinanceUsdmMarketDataTests
{
    private const string BaseUrl = "https://fapi.binance.test";

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static BinanceUsdmMarketData Adapter(
        IBinanceOpenInterestFeed? oi = null,
        FixtureHandler? handler = null,
        IBinanceLiveFeed? ws = null,
        TimeProvider? clock = null) =>
        new(new BinanceUsdmClient(new HttpClient(handler ?? new FixtureHandler()), BaseUrl),
            oi ?? StubOpenInterestFeed.ForEverything(),
            ws,
            clock ?? new FakeTimeProvider(Now));

    // ── Discovery: scope ─────────────────────────────────────────────────
    [Fact]
    public async Task Discovery_carries_usd_quoted_perpetuals_and_reads_the_increments_out_of_the_filters()
    {
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "BTCUSDT", "ETHUSDT", "DOGEUSDT", "1000PEPEUSDT", "OMGUSDT", "GAIBUSDT", "BTCUSDC", "XTZUSDT" },
            instruments.Select(i => i.ExchangeSymbol).ToArray());

        var btc = instruments.Single(i => i.ExchangeSymbol == "BTCUSDT");
        Assert.Equal("BTC", btc.BaseAssetRaw);
        Assert.Equal("USDT", btc.QuoteAssetRaw);
        Assert.Equal(1m, btc.ContractMultiplier);   // USDⓈ-M quantities are already base-asset units
        Assert.Equal(0.10m, btc.PriceStep);         // PRICE_FILTER.tickSize
        Assert.Equal(0.001m, btc.QtyStep);          // LOT_SIZE.stepSize
        Assert.Equal(0.001m, btc.MinQty);           // LOT_SIZE.minQty
        Assert.Equal(50m, btc.MinNotional);         // MIN_NOTIONAL.notional — Binance does define one
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1567965300000), btc.ListedAt);
        Assert.Equal(InstrumentStatus.Trading, btc.Status);
        Assert.Contains("\"symbol\": \"BTCUSDT\"", btc.RawJson);

        // The venue's own spelling, NOT normalised here: the alias table is what turns 1000PEPE into
        // PEPE with a multiplier of 1000, and a second opinion in the adapter would be a second
        // definition of the asset.
        Assert.Equal("1000PEPE", instruments.Single(i => i.ExchangeSymbol == "1000PEPEUSDT").BaseAssetRaw);

        // USDC is USD-family and in scope; the venue also quotes perpetuals in USD1, "U" and BTC,
        // which are not (see BinanceMarkets.UsdFamily).
        Assert.Equal("USDC", instruments.Single(i => i.ExchangeSymbol == "BTCUSDC").QuoteAssetRaw);
    }

    [Fact]
    public async Task Discovery_excludes_the_undocumented_tradfi_contract_type_and_the_dated_ones()
    {
        // The reason the filter is an allowlist. XAUUSDT arrives as contractType
        // TRADIFI_PERPETUAL — a value in no documentation available to us, carried live by 191
        // symbols (XAU, TSLA, NVDA) — so a denylist written from the documented vocabulary would
        // have admitted every equity and metal perpetual on the venue without a word.
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "XAUUSDT");
        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "BTCUSDT_260925");
    }

    // ── Discovery: status ────────────────────────────────────────────────
    [Fact]
    public async Task Settling_and_pending_contracts_are_halted_rather_than_dropped()
    {
        // Listed, not trading. Dropping them would make discovery lose sight of the symbol and write
        // 'delisted' three passes later — a lifecycle event the venue never announced. Halted is
        // what is actually observable, and the collectors skip it anyway because collect follows
        // status. Same rule, same reason, as WEEX's dead tail.
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        Assert.Equal(InstrumentStatus.Halted, instruments.Single(i => i.ExchangeSymbol == "OMGUSDT").Status);
        Assert.Equal(InstrumentStatus.Halted, instruments.Single(i => i.ExchangeSymbol == "GAIBUSDT").Status);
    }

    [Fact]
    public async Task An_unknown_status_fails_the_whole_pass_instead_of_omitting_the_instrument()
    {
        // The asymmetry with the contract-type allowlist, pinned. An unknown status arrives on an
        // instrument we are ALREADY tracking, and the quiet answer — leave it out of this pass — is
        // written as a delisting after delist_after_missed_discoveries. Throwing returns before the
        // delisting sweep runs, so nothing is written at all and a person is asked what the new word
        // means.
        var handler = new FixtureHandler(exchangeInfo: File
            .ReadAllText(Path.Combine(FixtureDir, "exchangeinfo.json"))
            .Replace("\"status\": \"TRADING\"", "\"status\": \"AUCTION_MATCH\"", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Adapter(handler: handler).GetInstrumentsAsync(CancellationToken.None));

        Assert.Contains("AUCTION_MATCH", error.Message, StringComparison.Ordinal);
        Assert.Contains("delisting", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_funding_interval_comes_from_fundingInfo_and_defaults_to_eight_hours_when_absent()
    {
        // fundingInfo lists only the symbols whose terms deviate, so absence is a statement and not
        // a gap: 455 of the 780 rows live today are on a 4-hour cycle, so a single hard-coded
        // constant would have been wrong for most of the venue — while OMGUSDT, absent from the
        // batch, really is on the 8-hour default.
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        Assert.Equal((short)8, instruments.Single(i => i.ExchangeSymbol == "BTCUSDT").FundingIntervalHours);
        Assert.Equal((short)4, instruments.Single(i => i.ExchangeSymbol == "XTZUSDT").FundingIntervalHours);
        Assert.Equal((short)8, instruments.Single(i => i.ExchangeSymbol == "OMGUSDT").FundingIntervalHours);
    }

    // ── Ticker merge: happy path + honest omission ──────────────────────
    [Fact]
    public async Task Tickers_merge_top_of_book_mark_price_turnover_and_cached_open_interest()
    {
        var oiAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var tickers = await Adapter(StubOpenInterestFeed.ForEverything(106760.161, oiAt))
            .GetTickersAsync(CancellationToken.None);

        var btc = tickers.Single(t => t.ExchangeSymbol == "BTCUSDT");
        Assert.Equal(79919.30, btc.LastPrice);        // ticker/24hr — no cheaper batch carries it
        Assert.Equal(79919.20, btc.BidPrice);         // bookTicker
        Assert.Equal(9.735, btc.BidSize);
        Assert.Equal(79919.30, btc.AskPrice);
        Assert.Equal(1.742, btc.AskSize);
        Assert.Equal(79919.30, btc.MarkPrice);        // premiumIndex
        Assert.Equal(79955.90282609, btc.IndexPrice, 8);
        Assert.Equal(0.00004374, btc.FundingRate, 10); // already a fraction per interval; used as-is
        Assert.Equal(4396232143.81, btc.Turnover24h, 2);
        Assert.Equal(106760.161, btc.OpenInterest);
        Assert.Equal(oiAt, btc.OpenInterestAt);        // OI's own, older time — not ReceivedAt
        Assert.Null(btc.Depth);                        // the book is a separate pass
    }

    [Fact]
    public async Task A_symbol_missing_from_the_book_ticker_batch_is_omitted_not_defaulted()
    {
        // ETHUSDT is present in premiumIndex, in ticker/24hr and in the OI cache — everything except
        // the batch that carries bid/ask and their sizes. Shipping it would mean inventing a top of
        // book, so the row simply ages instead.
        var tickers = await Adapter().GetTickersAsync(CancellationToken.None);
        Assert.DoesNotContain(tickers, t => t.ExchangeSymbol == "ETHUSDT");
    }

    [Fact]
    public async Task A_symbol_with_no_open_interest_sample_yet_is_omitted()
    {
        var tickers = await Adapter(StubOpenInterestFeed.ForNothing()).GetTickersAsync(CancellationToken.None);
        Assert.Empty(tickers);   // every symbol waits on the same never-sampled feed
    }

    [Fact]
    public async Task Out_of_scope_symbols_never_reach_the_batch_because_open_interest_never_samples_them()
    {
        // The OI cycle's symbol list is filtered by the same scope rule discovery applies, so an
        // equity perpetual or a quarterly is never sampled and therefore never merged. That is the
        // second of two independent gates — the first is discovery, and SnapshotCollector ignores
        // any ticker whose symbol it has no instrument row for.
        var inScope = (await Adapter().GetInstrumentsAsync(CancellationToken.None))
            .Select(i => i.ExchangeSymbol)
            .ToHashSet(StringComparer.Ordinal);

        var tickers = await Adapter(StubOpenInterestFeed.For(inScope)).GetTickersAsync(CancellationToken.None);

        Assert.DoesNotContain(tickers, t => t.ExchangeSymbol == "XAUUSDT");
        Assert.DoesNotContain(tickers, t => t.ExchangeSymbol == "BTCUSDT_260925");
        Assert.Contains(tickers, t => t.ExchangeSymbol == "BTCUSDT");
    }

    // ── The clock ────────────────────────────────────────────────────────
    [Fact]
    public async Task Received_at_is_our_own_read_and_not_the_oldest_of_the_three_batched_venue_clocks()
    {
        // The defect this test exists to prevent. market_snapshot is keyed on
        // (exchange_instrument_id, received_at) with ON CONFLICT DO NOTHING, so a received_at that
        // can repeat turns the insert into a silent no-op: bookTicker.time only moves when the top
        // of book moves, so on a quiet symbol two consecutive passes would present the same instant
        // and the second observation would vanish without a trace. Our own read moves every pass.
        var tickers = await Adapter().GetTickersAsync(CancellationToken.None);
        var btc = tickers.Single(t => t.ExchangeSymbol == "BTCUSDT");

        Assert.Equal(Now, btc.ReceivedAt);

        // Every symbol in a pass shares that one read, and none of them carries a venue clock.
        Assert.All(tickers, t => Assert.Equal(Now, t.ReceivedAt));
        Assert.DoesNotContain(tickers, t => t.ReceivedAt == DateTimeOffset.FromUnixTimeMilliseconds(1788695323593));
    }

    [Fact]
    public async Task Open_interest_keeps_the_venue_sampling_time_rather_than_the_pass_clock()
    {
        // open_interest_at is a separate column precisely because this number is measured on a
        // different schedule from the snapshot it travels with. Collapsing it onto ReceivedAt would
        // erase the distinction the column was added for.
        var oiAt = new DateTimeOffset(2026, 9, 6, 11, 47, 0, TimeSpan.Zero);
        var tickers = await Adapter(StubOpenInterestFeed.ForEverything(1.0, oiAt)).GetTickersAsync(CancellationToken.None);

        Assert.All(tickers, t => Assert.Equal(oiAt, t.OpenInterestAt));
        Assert.All(tickers, t => Assert.NotEqual(t.ReceivedAt, t.OpenInterestAt));
    }

    // ── Candles ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Candles_drop_the_forming_bar_and_carry_the_venue_trade_count()
    {
        // `to` sits exactly on the newest bar's open time, so that bar is still forming — Binance
        // returns it as a normal row with partial volume, which is the value that would be wrong
        // forever once stored.
        var from = DateTimeOffset.FromUnixTimeMilliseconds(1788695820000);
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1788696120000);

        var candles = await Adapter().GetCandles1mAsync("BTCUSDT", from, to, CancellationToken.None);

        Assert.Equal(5, candles.Count);
        for (var i = 1; i < candles.Count; i++)
        {
            Assert.Equal(TimeSpan.FromMinutes(1), candles[i].OpenTime - candles[i - 1].OpenTime);
        }

        var first = candles[0];
        Assert.Equal(from, first.OpenTime);
        Assert.Equal(79882.40, first.Open);
        Assert.Equal(79882.50, first.High);
        Assert.Equal(79871.60, first.Low);
        Assert.Equal(79871.60, first.Close);
        Assert.Equal(9.848, first.Volume, 4);   // index 5, base units — not index 7, the quote value

        // Binance is the first venue here that reports a trade count at all; Kraken and WEEX both
        // carry null, so this column finally holds an observation rather than an absence.
        Assert.Equal(636, first.TradeCount);
    }

    // ── Funding history ──────────────────────────────────────────────────
    [Fact]
    public async Task Funding_history_is_oldest_first_and_windowed_at_both_edges()
    {
        // The fixture's six payments sit on 8 h boundaries. This window (edges inclusive) keeps the
        // middle four: the server already applies startTime/endTime, and the adapter re-applies them
        // so a venue that widens its reading of the bounds cannot widen ours.
        var from = DateTimeOffset.FromUnixTimeMilliseconds(1788566400000);
        var to = DateTimeOffset.FromUnixTimeMilliseconds(1788652800000);

        var rates = await Adapter().GetFundingHistoryAsync("BTCUSDT", from, to, CancellationToken.None);

        Assert.Equal(4, rates.Count);
        Assert.Equal(from, rates[0].FundingTime);
        Assert.Equal(0.00001014, rates[0].Rate, 10);
        Assert.Equal(-0.00000150, rates[1].Rate, 10);   // funding is signed; a negative is not an error
        for (var i = 1; i < rates.Count; i++)
        {
            Assert.True(rates[i].FundingTime > rates[i - 1].FundingTime);
        }
    }

    // ── Depth ────────────────────────────────────────────────────────────
    [Fact]
    public async Task A_book_that_reaches_past_the_bands_sums_them_and_carries_the_venue_clock()
    {
        // DOGEUSDT at limit=100 — the level this adapter asks for — reaches ~111 bps from mid, so
        // all three bands are bounded and real. Values computed directly from the fixture, not
        // hand-derived.
        var depth = await Adapter().GetOrderBookAsync("DOGEUSDT", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.Equal(286792.21, depth!.Bid10Bps!.Value, 2);
        Assert.Equal(384746.91, depth.Ask10Bps!.Value, 2);
        Assert.Equal(829993.86, depth.Bid25Bps!.Value, 2);
        Assert.Equal(873307.85, depth.Ask25Bps!.Value, 2);
        Assert.Equal(2709597.52, depth.Bid50Bps!.Value, 2);
        Assert.Equal(2437286.785, depth.Ask50Bps!.Value, 3);

        // The venue's E, not our clock: depth_at is a separate column because the book runs on its
        // own schedule.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1788696160425), depth.At);
    }

    [Fact]
    public async Task A_book_that_does_not_reach_past_a_band_leaves_it_null_rather_than_undercounting()
    {
        // BTCUSDT is the measured worst case on this venue: at a 0.10 tick, even 1000 levels span
        // only ~17 bps, and the 100 this adapter asks for span 1.4. Every band is honestly null —
        // an unbounded sum would be an undercount wearing a real number's clothes. This is the
        // single strongest argument for the WebSocket book, which maintains every level the venue
        // publishes rather than a window of them.
        var depth = await Adapter(handler: new FixtureHandler(depthFile: "depth_shallow.json"))
            .GetOrderBookAsync("BTCUSDT", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.Null(depth!.Bid10Bps);
        Assert.Null(depth.Ask10Bps);
        Assert.Null(depth.Bid25Bps);
        Assert.Null(depth.Ask25Bps);
        Assert.Null(depth.Bid50Bps);
        Assert.Null(depth.Ask50Bps);
    }

    [Fact]
    public async Task Depth_prefers_the_live_feed_and_falls_back_to_rest_when_it_cannot_serve()
    {
        var live = new Depth(1, 2, 3, 4, 5, 6, Now);

        var served = await Adapter(ws: new StubLiveFeed(live)).GetOrderBookAsync("DOGEUSDT", CancellationToken.None);
        Assert.Same(live, served);

        // An unhealthy feed is a coarser cadence, not an outage: the REST book answers instead.
        var fallback = await Adapter(ws: new StubLiveFeed(null)).GetOrderBookAsync("DOGEUSDT", CancellationToken.None);
        Assert.NotNull(fallback);
        Assert.NotSame(live, fallback);
    }

    // ── Capabilities ─────────────────────────────────────────────────────
    [Fact]
    public void Only_depth_is_declared_over_two_transports()
    {
        var capabilities = Adapter().Capabilities.ToDictionary(c => c.DatasetCode, c => c.TransportsUs, StringComparer.Ordinal);

        Assert.Equal("rest,ws", capabilities["depth"]);
        Assert.Equal("rest", capabilities["snapshot"]);
        Assert.Equal("rest", capabilities["discovery"]);
        Assert.Equal("rest", capabilities["candles"]);
        Assert.Equal("rest", capabilities["funding"]);
    }

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "binance");

    /// <summary>Replays a fixture per endpoint, routed by path exactly as the client builds them.</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string? _exchangeInfo;
        private readonly string _depthFile;

        public FixtureHandler(string? exchangeInfo = null, string depthFile = "depth.json")
        {
            _exchangeInfo = exchangeInfo;
            _depthFile = depthFile;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/fapi/v1/exchangeInfo", StringComparison.Ordinal))
            {
                return _exchangeInfo is not null ? Raw(_exchangeInfo) : Json("exchangeinfo.json");
            }

            if (path.EndsWith("/fapi/v1/fundingInfo", StringComparison.Ordinal))
            {
                return Json("fundinginfo.json");
            }

            if (path.EndsWith("/fapi/v1/ticker/bookTicker", StringComparison.Ordinal))
            {
                return Json("bookticker.json");
            }

            if (path.EndsWith("/fapi/v1/premiumIndex", StringComparison.Ordinal))
            {
                return Json("premiumindex.json");
            }

            if (path.EndsWith("/fapi/v1/ticker/24hr", StringComparison.Ordinal))
            {
                return Json("ticker24hr.json");
            }

            if (path.EndsWith("/fapi/v1/openInterest", StringComparison.Ordinal))
            {
                return Json("openinterest.json");
            }

            if (path.EndsWith("/fapi/v1/klines", StringComparison.Ordinal))
            {
                return Json("klines.json");
            }

            if (path.EndsWith("/fapi/v1/fundingRate", StringComparison.Ordinal))
            {
                return Json("fundingrate.json");
            }

            if (path.EndsWith("/fapi/v1/depth", StringComparison.Ordinal))
            {
                return Json(_depthFile);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string fixture) =>
            Raw(File.ReadAllText(Path.Combine(FixtureDir, fixture)));

        private static Task<HttpResponseMessage> Raw(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>A fixed answer for a known set of symbols — enough to prove the merge logic without
    /// driving the real background cycle.</summary>
    private sealed class StubOpenInterestFeed : IBinanceOpenInterestFeed
    {
        private readonly IReadOnlySet<string>? _known;
        private readonly double? _value;
        private readonly DateTimeOffset _at;

        private StubOpenInterestFeed(IReadOnlySet<string>? known, double? value, DateTimeOffset at)
        {
            _known = known;
            _value = value;
            _at = at;
        }

        public static StubOpenInterestFeed ForEverything(
            double value = 106760.161, DateTimeOffset? at = null) =>
            new(null, value, at ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        public static StubOpenInterestFeed ForNothing() => new(null, null, default);

        /// <summary>Mirrors the real feed, whose symbol list is scope-filtered the same way discovery
        /// is: a symbol we do not carry is never sampled.</summary>
        public static StubOpenInterestFeed For(IReadOnlySet<string> symbols) =>
            new(symbols, 106760.161, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
        {
            if (_value is { } v && (_known is null || _known.Contains(symbol)))
            {
                openInterest = v;
                at = _at;
                return true;
            }

            openInterest = 0;
            at = default;
            return false;
        }
    }

    private sealed class StubLiveFeed : IBinanceLiveFeed
    {
        private readonly Depth? _depth;

        public StubLiveFeed(Depth? depth) => _depth = depth;

        public bool TryGetDepth(string symbol, out Depth depth)
        {
            depth = _depth!;
            return _depth is not null;
        }
    }
}

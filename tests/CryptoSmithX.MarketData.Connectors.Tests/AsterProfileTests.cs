using System.Net;
using CryptoSmithX.MarketData.Connectors.Binance;
using CryptoSmithX.MarketData.Connectors.Market;
using CryptoSmithX.MarketData.Connectors.Pacing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Aster (asterdex) as a <see cref="BinanceUsdmProfile"/> over the same classes Binance USDⓈ-M
/// already uses — plans/aster-venue-blueprint.md §4. Fixtures in Fixtures/aster and Fixtures/aster-ws
/// are live captures, 2026-09-16, public endpoints only (see Fixtures/aster/README.md).
///
/// The one test this file exists to make impossible to skip is
/// <see cref="Binance_profile_reproduces_every_constant_it_replaced"/>: everything else here proves
/// Aster works, that one proves Binance still does, byte for byte.
/// </summary>
public sealed class AsterProfileTests
{
    private const string BaseUrl = "https://fapi.asterdex.test";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "aster");

    // ── The pin: Binance is unchanged ───────────────────────────────────────────────────────
    [Fact]
    public void Binance_profile_reproduces_every_constant_it_replaced()
    {
        var p = BinanceUsdmProfile.Binance;

        Assert.Equal("binance-usdm", p.SegmentCode);
        Assert.Equal("Binance.Usdm", p.LogName);
        Assert.Equal("/fapi/v1", p.ApiPrefix);
        Assert.Equal(100, p.RestDepthLimit);                 // BinanceUsdmClient.DepthLimit, the old constant
        Assert.Equal(OpenInterestHistoryMode.Analytics, p.OpenInterestHistory);
        Assert.Equal(FeedSymbolsMode.WholeVenue, p.FeedSymbols);
        Assert.Equal(1024, p.MaxStreamsPerConnection);
        Assert.True(p.TickersFromMarketFeed);
        Assert.Null(p.KnownExcludedSymbolTypes);

        // A client built with no profile at all still gets exactly this depth limit — the default
        // parameter on BinanceUsdmClient's own ctor, not something only the profile enforces.
        var client = new BinanceUsdmClient(BaseUrl);
        Assert.Equal(BinanceUsdmClient.DepthLimit, client.RestDepthLimit);
        Assert.Equal(100, client.RestDepthLimit);
    }

    [Fact]
    public void Aster_profile_narrows_scope_to_symbolType_zero_and_keeps_binances_own_rule_underneath()
    {
        var p = BinanceUsdmProfile.Aster;
        Assert.Equal("aster-perp", p.SegmentCode);
        Assert.Equal("Aster.Perp", p.LogName);
        Assert.Equal("/fapi/v1", p.ApiPrefix);          // same path generation as Binance — blueprint §1.5
        Assert.Equal(500, p.RestDepthLimit);
        Assert.Equal(OpenInterestHistoryMode.Sampled, p.OpenInterestHistory);
        Assert.Equal(FeedSymbolsMode.Collected, p.FeedSymbols);
        Assert.Equal(200, p.MaxStreamsPerConnection);
        Assert.False(p.TickersFromMarketFeed);
        Assert.Equal(new HashSet<int> { 1 }, p.KnownExcludedSymbolTypes);
    }

    // ── Discovery: scope ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Discovery_carries_only_symbolType_zero_usd_family_perpetuals()
    {
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        // In scope: BTCUSDT and ASTERUSDT (symbolType 0, USDT), 1000SHIBUSDT (same, 1000-prefixed),
        // and TONUSDT — symbolType 0 too, merely SETTLING (see the status test below): in scope and
        // listed, just not tradable right now, which is a different question from scope.
        Assert.Equal(
            new[] { "1000SHIBUSDT", "ASTERUSDT", "BTCUSDT", "TONUSDT" },
            instruments.Select(i => i.ExchangeSymbol).OrderBy(s => s, StringComparer.Ordinal).ToArray());

        // TSLAUSDT: contractType PERPETUAL (carried by Binance's own allowlist), symbolType 1 — the
        // documented RWA marker (blueprint §1.2) — excluded by Aster's extra check, quietly: it is a
        // known, deliberate exclusion, not a surprise.
        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "TSLAUSDT");

        // XAUUSD1, BTCUSD1: quoteAsset USD1, out via BinanceMarkets.UsdFamily regardless of symbolType.
        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "XAUUSD1");
        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "BTCUSD1");

        // MBLUSDT: contractType "" — already excluded and logged once by the existing contract-type
        // allowlist (BinanceMarkets.IsCarriedContract), unchanged by this profile.
        Assert.DoesNotContain(instruments, i => i.ExchangeSymbol == "MBLUSDT");
    }

    [Fact]
    public async Task Symbol_type_one_is_excluded_quietly_but_an_unrecognised_value_is_logged_once()
    {
        var log = new CapturingLogger();

        // TSLAUSDT (symbolType 1, documented) must not be reported: it is a known exclusion.
        await Adapter(log: log).GetInstrumentsAsync(CancellationToken.None);
        Assert.DoesNotContain(log.Messages, m => m.Contains("TSLAUSDT", StringComparison.Ordinal));

        // A symbolType this profile has never seen — 7, say — on a symbol that would otherwise be in
        // scope (contractType PERPETUAL, quote USDT): reported once, the ReportUnknownContractType
        // pattern. A minimal, self-contained payload — not a mutation of the shared multi-symbol
        // fixture — so this is the only symbol in play and the assertion cannot be satisfied by
        // some other row's coincidence.
        // Two symbols share the never-seen value 7, to prove the "once per distinct VALUE, not once
        // per symbol" dedup ReportUnknownContractType already has.
        const string TwoSymbols = """
            {"symbols":[
              {
                "symbol": "UNKNOWNTYPEUSDT", "contractType": "PERPETUAL", "status": "TRADING",
                "baseAsset": "UNKNOWNTYPE", "quoteAsset": "USDT", "onboardDate": 1700000000000,
                "symbolType": 7,
                "filters": [
                  {"filterType": "PRICE_FILTER", "tickSize": "0.1"},
                  {"filterType": "LOT_SIZE", "stepSize": "0.001", "minQty": "0.001"},
                  {"filterType": "MIN_NOTIONAL", "notional": "5"}
                ]
              },
              {
                "symbol": "SECONDUNKNOWNUSDT", "contractType": "PERPETUAL", "status": "TRADING",
                "baseAsset": "SECONDUNKNOWN", "quoteAsset": "USDT", "onboardDate": 1700000000000,
                "symbolType": 7,
                "filters": [
                  {"filterType": "PRICE_FILTER", "tickSize": "0.1"},
                  {"filterType": "LOT_SIZE", "stepSize": "0.001", "minQty": "0.001"},
                  {"filterType": "MIN_NOTIONAL", "notional": "5"}
                ]
              }
            ]}
            """;

        var log2 = new CapturingLogger();
        var handler = new FixtureHandler(exchangeInfo: TwoSymbols);
        var instruments = await Adapter(handler: handler, log: log2).GetInstrumentsAsync(CancellationToken.None);

        Assert.Empty(instruments);   // symbolType 7 is not 0: out of scope, same as symbolType 1

        Assert.Contains(log2.Messages, m =>
            m.Contains("symbolType", StringComparison.Ordinal) && m.Contains("UNKNOWNTYPEUSDT", StringComparison.Ordinal));

        // And it is reported only once even though the JSON below still carries BTCUSDT itself too —
        // the same "once per distinct value per process" rule ReportUnknownContractType already has.
        var count = log2.Messages.Count(m => m.Contains("symbolType", StringComparison.Ordinal) && m.Contains("'7'", StringComparison.Ordinal));
        Assert.True(count <= 1, $"expected the unknown-symbolType warning at most once, saw {count}");
    }

    [Fact]
    public async Task Status_mapping_is_reused_unchanged_settling_maps_to_halted()
    {
        // TONUSDT: status SETTLING on the live venue — the same status Binance's OMGUSDT fixture
        // pins, mapped by the same BinanceMarkets.Status switch, unmodified by this profile. SETTLING
        // symbols are out of Aster's own scope (quoteAsset here happens to be USDT and symbolType 0
        // is NOT what excludes it — TONUSDT is genuinely in-scope by symbolType, it is simply halted,
        // not delisted), so it must still surface as Halted rather than vanish.
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);
        var ton = instruments.SingleOrDefault(i => i.ExchangeSymbol == "TONUSDT");
        Assert.NotNull(ton);
        Assert.Equal(InstrumentStatus.Halted, ton!.Status);
    }

    [Fact]
    public async Task Funding_interval_reads_asters_own_fundingInfo_same_default_asymmetry_as_binance()
    {
        var instruments = await Adapter().GetInstrumentsAsync(CancellationToken.None);

        // ASTERUSDT carries a 4 h interval in fundingInfo, live; BTCUSDT is absent from no-deviation
        // rows and therefore defaults to 8 h — the same asymmetry Binance's XTZUSDT/BTCUSDT pair pins.
        Assert.Equal((short)4, instruments.Single(i => i.ExchangeSymbol == "ASTERUSDT").FundingIntervalHours);
        Assert.Equal((short)8, instruments.Single(i => i.ExchangeSymbol == "BTCUSDT").FundingIntervalHours);
    }

    // ── REST depth at limit 500 ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Depth_asks_for_limit_500_under_the_aster_profile()
    {
        string? requestedQuery = null;
        var handler = new FixtureHandler(onDepthRequested: q => requestedQuery = q);

        var depth = await Adapter(handler: handler).GetOrderBookAsync("BTCUSDT", CancellationToken.None);

        Assert.NotNull(depth);
        Assert.NotNull(requestedQuery);
        Assert.Contains("limit=500", requestedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("limit=100", requestedQuery, StringComparison.Ordinal);
    }

    // ── Open interest history: [] without ever calling the venue ───────────────────────────
    [Fact]
    public async Task Open_interest_history_returns_empty_and_never_calls_the_404_endpoint()
    {
        var called = false;
        var handler = new FixtureHandler(onOpenInterestHistRequested: () => called = true);

        var history = await Adapter(handler: handler)
            .GetOpenInterestHistoryAsync("BTCUSDT", Now.AddHours(-1), Now, CancellationToken.None);

        Assert.Empty(history);
        Assert.False(called, "the profile says Sampled; /futures/data/openInterestHist must never be requested");
    }

    // ── Ticker path ignores the market feed under the Aster profile ────────────────────────
    [Fact]
    public async Task Ticker_path_stays_on_rest_even_with_a_healthy_market_feed_under_the_aster_profile()
    {
        var feed = new StubMarketFeed
        {
            ContextsOk = true,
            Contexts = [new BinanceContext("BTCUSDT", LastPrice: 1, MarkPrice: 2, IndexPrice: 3, FundingRate: 4, Turnover24h: 5, Now)],
        };

        var tickers = await Adapter(marketFeed: feed).GetTickersAsync(CancellationToken.None);

        // Had the WS branch been taken, LastPrice would be 1 (from the stub context) and none of
        // premiumIndex/ticker24hr would have been read. It is instead the REST-composed value —
        // proof the profile's TickersFromMarketFeed=false was honoured.
        var btc = Assert.Single(tickers, t => t.ExchangeSymbol == "BTCUSDT");
        Assert.NotEqual(1, btc.LastPrice);
    }

    [Fact]
    public void Snapshot_capability_is_rest_only_under_the_aster_profile()
    {
        var capabilities = Adapter().Capabilities.ToDictionary(c => c.DatasetCode, c => c.TransportsUs, StringComparer.Ordinal);
        Assert.Equal("rest", capabilities["snapshot"]);
        // Everything else is unaffected by the profile.
        Assert.Equal("rest,ws", capabilities["depth"]);
    }

    [Fact]
    public void Segment_code_and_capabilities_come_from_the_profile()
    {
        Assert.Equal("aster-perp", Adapter().SegmentCode);
    }

    // ── A bodiless 429 penalises the gate ───────────────────────────────────────────────────
    [Fact]
    public async Task A_bodiless_429_is_a_refusal_not_a_parse_failure_and_penalises_the_gate()
    {
        // Fixtures/aster/README.md: CloudFront answered HTTP 429 with an empty body, no Retry-After,
        // no x-mbx-* header — a second limiter invisible in exchangeInfo.rateLimits. Not re-provoked
        // here (the blueprint is explicit that must not be done from a collector IP); this response
        // is built to match the shape observed, exactly as described.
        var handler = new FixtureHandler(bodilessTooManyRequests: true);
        var client = new BinanceUsdmClient(new HttpClient(handler), BaseUrl, BinanceUsdmProfile.Aster.ApiPrefix, BinanceUsdmProfile.Aster.RestDepthLimit);
        var gate = new VenueGate("aster-perp-test", requestsPerSecond: 5, maxConcurrentRequests: 4, new FakeTimeProvider(Now));

        Assert.Equal(default, gate.PenaltyUntil);

        var ex = await Assert.ThrowsAsync<VenueRateLimitedException>(
            () => client.GetSymbolsAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Null(ex.RetryAfter);   // no Retry-After header — the gate falls back to its own default

        VenuePenalty.Apply(gate, ex);
        Assert.True(gate.PenaltyUntil > Now);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────
    private static BinanceUsdmMarketData Adapter(
        FixtureHandler? handler = null,
        IBinanceMarketFeed? marketFeed = null,
        CapturingLogger? log = null) =>
        new(
            new BinanceUsdmClient(new HttpClient(handler ?? new FixtureHandler()), BaseUrl, BinanceUsdmProfile.Aster.ApiPrefix, BinanceUsdmProfile.Aster.RestDepthLimit),
            StubOpenInterestFeed.ForEverything(),
            ws: null,
            marketFeed: marketFeed,
            clock: new FakeTimeProvider(Now),
            log: (Microsoft.Extensions.Logging.ILogger?)log ?? NullLogger.Instance,
            profile: BinanceUsdmProfile.Aster);

    /// <summary>Replays a fixture per endpoint, routed by path exactly as the client builds them —
    /// same shape as <c>BinanceUsdmMarketDataTests.FixtureHandler</c>, extended with the hooks this
    /// file's tests need (a bodiless 429, a spy on the depth query string, a spy on whether the
    /// openInterestHist path is ever touched).</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string? _exchangeInfo;
        private readonly Action<string>? _onDepthRequested;
        private readonly Action? _onOpenInterestHistRequested;
        private readonly bool _bodilessTooManyRequests;

        public FixtureHandler(
            string? exchangeInfo = null,
            Action<string>? onDepthRequested = null,
            Action? onOpenInterestHistRequested = null,
            bool bodilessTooManyRequests = false)
        {
            _exchangeInfo = exchangeInfo;
            _onDepthRequested = onDepthRequested;
            _onOpenInterestHistRequested = onOpenInterestHistRequested;
            _bodilessTooManyRequests = bodilessTooManyRequests;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_bodilessTooManyRequests)
            {
                // No Content set at all — the CloudFront shape: HTTP 429, empty body, no headers of
                // ours to read. EnsureVenueSuccess must throw before anything tries to parse this as
                // JSON.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            }

            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri!.Query;

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

            if (path.EndsWith("/fapi/v1/markPriceKlines", StringComparison.Ordinal))
            {
                return Json("markpriceklines.json");
            }

            if (path.EndsWith("/fapi/v1/indexPriceKlines", StringComparison.Ordinal))
            {
                return Json("indexpriceklines.json");
            }

            if (path.EndsWith("/fapi/v1/fundingRate", StringComparison.Ordinal))
            {
                return Json("fundingrate.json");
            }

            if (path.EndsWith("/fapi/v1/depth", StringComparison.Ordinal))
            {
                _onDepthRequested?.Invoke(query);
                return Json("depth.json");
            }

            if (path.EndsWith("/futures/data/openInterestHist", StringComparison.Ordinal))
            {
                _onOpenInterestHistRequested?.Invoke();
                return Html(Path.Combine(FixtureDir, "openinteresthist_404.html"), HttpStatusCode.NotFound);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string fixture) =>
            Raw(File.ReadAllText(Path.Combine(FixtureDir, fixture)));

        private static Task<HttpResponseMessage> Raw(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

        private static Task<HttpResponseMessage> Html(string path, HttpStatusCode status) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(File.ReadAllText(path), System.Text.Encoding.UTF8, "text/html"),
        });
    }

    private sealed class StubOpenInterestFeed : IBinanceOpenInterestFeed
    {
        private readonly double _value;
        private readonly DateTimeOffset _at;

        private StubOpenInterestFeed(double value, DateTimeOffset at)
        {
            _value = value;
            _at = at;
        }

        public static StubOpenInterestFeed ForEverything() => new(106760.161, new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));

        public bool TryGet(string symbol, out double openInterest, out DateTimeOffset at)
        {
            openInterest = _value;
            at = _at;
            return true;
        }
    }

    private sealed class StubMarketFeed : IBinanceMarketFeed
    {
        public bool ContextsOk { get; init; }
        public IReadOnlyList<BinanceContext> Contexts { get; init; } = [];

        public bool TryGetFreshContexts(out IReadOnlyList<BinanceContext> contexts)
        {
            contexts = Contexts;
            return ContextsOk;
        }

        public bool TryGetCandles1m(string symbol, DateTimeOffset from, DateTimeOffset to, out IReadOnlyList<Candle> candles)
        {
            candles = [];
            return false;
        }
    }

    /// <summary>Every message logged, flattened to text, so a test can assert on wording without
    /// needing a real sink.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoSmithX.MarketData.Connectors;
using CryptoSmithX.MarketData.Connectors.Avantis;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The first venue here with no book, no discrete funding and no price of its own — so most of what
/// this adapter must get right is what it REFUSES to fill.
///
/// Every fixture below is a trimmed copy of the live catalogue (data.avantisfi.com/v2/trading,
/// 2026-09-11), keeping the shape and the values exactly as the venue sent them.
/// </summary>
public sealed class AvantisMarketDataTests
{
    private const string BtcPair = """
        {"index":1,"from":"BTC","to":"USD","groupIndex":0,"isPairListed":true,
         "openInterest":{"long":2548348.23,"short":2865037.88},
         "coinOI":{"long":36.1907853204,"short":35.9601677791},
         "pairOI":5413386.109999999,"pairMaxOI":8446867.015,"blockOILimit":2500000,
         "liquidity":{"buy":8446867.015,"sell":8446867.015},
         "pairParams":{"onePercentDepthAbove":88707556.896,"onePercentDepthBelow":115815456.472},
         "priceImpactMultiplier":0,"skewImpactMultiplier":0,"spreadP":0,"decayedVol":78.479129,
         "minLevPosUSDC":100,"leverages":{"minLeverage":1,"maxLeverage":50},
         "accPerOiLong":552.6,"accPerOiShort":-567.8,"fundingRate":{"long":0.0012,"short":-0.0012},
         "fundingLastUpdateBlock":51159136,
         "feed":{"feedId":"0xe62d","attributes":{"symbol":"Crypto.BTC/USD","assetType":"crypto",
                 "isOpen":true,"nextOpen":0,"nextClose":0,"schedule":"America/New_York;O,O,O,O,O,O,O;"}}}
        """;

    private const string ClosedEquity = """
        {"index":81,"from":"NVDA","to":"USD","groupIndex":6,"isPairListed":true,
         "openInterest":{"long":10,"short":20},"coinOI":{"long":1,"short":2},"pairOI":30,
         "feed":{"attributes":{"symbol":"Equity.US.NVDA/USD","assetType":"equity","isOpen":false}}}
        """;

    private static string Catalog(params string[] pairs)
    {
        var entries = string.Join(",", pairs.Select((p, i) => $"\"{i}\":{p}"));
        return "{\"dataVersion\":3,\"pairCount\":" + pairs.Length
               + ",\"pairInfos\":{" + entries + "}}";
    }

    [Fact]
    public async Task It_fills_no_quote_and_no_funding_with_anything_at_all()
    {
        // The plan's first "do not": no bid = ask = last, no oracle-minus-half-spread, no zero. An
        // absent side is a fact about this market and has to read as one.
        var t = Assert.Single(await Adapter(Catalog(BtcPair)).GetTickersAsync(CancellationToken.None));

        Assert.Null(t.BidPrice);
        Assert.Null(t.AskPrice);
        Assert.Null(t.BidSize);
        Assert.Null(t.AskSize);
        Assert.Null(t.FundingRate);
    }

    [Fact]
    public async Task It_fills_no_price_either_because_the_venue_publishes_none()
    {
        // Measured, not assumed: the catalogue has no price field, and the venue's own Socket.IO
        // broadcast carries none either. The candle shim is the only place Avantis serves a price
        // and those are the ORACLE's bars, which land as index candles — never as this row.
        var t = Assert.Single(await Adapter(Catalog(BtcPair)).GetTickersAsync(CancellationToken.None));

        Assert.Null(t.LastPrice);
        Assert.Null(t.MarkPrice);
        Assert.Null(t.IndexPrice);
    }

    [Fact]
    public async Task Open_interest_is_written_in_both_units_and_derived_in_neither()
    {
        // Both are published, so both are recorded. pairOI was verified to equal long + short on
        // all 120 pairs, which is what makes it the VENUE's definition rather than our arithmetic.
        var t = Assert.Single(await Adapter(Catalog(BtcPair)).GetTickersAsync(CancellationToken.None));

        Assert.Equal(36.1907853204 + 35.9601677791, t.OpenInterest!.Value, 8);
        Assert.Equal(5413386.109999999, t.OiQuote!.Value, 6);
    }

    [Fact]
    public async Task A_closed_instrument_is_in_the_batch_and_says_it_is_closed()
    {
        // Dropping it would freeze its received_at, and a market shut for the weekend would read as
        // a feed that died — the exact confusion market_open exists to prevent.
        var tickers = await Adapter(Catalog(BtcPair, ClosedEquity)).GetTickersAsync(CancellationToken.None);

        Assert.Equal(2, tickers.Count);
        Assert.False(tickers.Single(x => x.ExchangeSymbol == "NVDA/USD").MarketOpen);
        Assert.True(tickers.Single(x => x.ExchangeSymbol == "BTC/USD").MarketOpen);
    }

    [Fact]
    public async Task It_declares_no_depth_and_never_returns_a_book()
    {
        // The two together are what stop a DepthCollector starting for a market with no book. The
        // precedent is the fake adapter, which declares none for the same reason.
        var adapter = Adapter(Catalog(BtcPair));

        Assert.DoesNotContain(adapter.Capabilities, c => c.DatasetCode == "depth");
        Assert.Null(await adapter.GetOrderBookAsync("BTC/USD", CancellationToken.None));
    }

    [Fact]
    public void It_declares_no_tape_and_no_market_candles()
    {
        // No public tape exists on any endpoint, so market candles cannot exist either — the shim's
        // bars carry no volume and are the oracle's price, which is candles_index.
        var caps = Adapter(Catalog(BtcPair)).Capabilities.Select(c => c.DatasetCode).ToArray();

        Assert.DoesNotContain("trades", caps);
        Assert.DoesNotContain("liquidations", caps);
        Assert.DoesNotContain("candles", caps);
        Assert.Contains("candles_index", caps);
    }

    [Fact]
    public async Task The_vault_state_is_scaled_out_of_on_chain_units()
    {
        // The venue sends these as decimal strings in its own scaling — USDC 1e6, ratio 1e10, both
        // from GET /v2/meta. A reader must never meet them in that shape.
        var http = new StubHandler(Catalog(BtcPair), """
            {"ok":true,"data":{"totalAssets":"10558583766171","totalSupply":"7764080943964",
             "utilizationRatio":"488908393315","depositCap":"500000000000000",
             "withdrawThreshold":"900000000000","sharePriceUsdc":1.3599270592843986}}
            """);
        var state = await new AvantisMarketData(new AvantisClient(new HttpClient(http), "https://data.test"))
            .GetVaultStateAsync(CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(10_558_583.766171, state!.TotalAssetsQuote!.Value, 6);
        Assert.Equal(0.4889083933, state.UtilizationRatio!.Value, 9);
        Assert.Equal(1.3599270592843986, state.SharePriceQuote);
    }

    [Fact]
    public async Task The_impact_inputs_are_carried_and_never_land_in_depth()
    {
        // "One percent depth" is an INPUT to a price function, not a measurement of a book. The
        // depth_*bps columns are documented as the latter, and this market has no book at all.
        var state = Assert.Single(await Adapter(Catalog(BtcPair)).GetVaultPairStateAsync(CancellationToken.None));

        Assert.Equal(88707556.896, state.DepthAbove1Pct);
        Assert.Equal(115815456.472, state.DepthBelow1Pct);
        Assert.Equal(2548348.23, state.OiLongQuote);
        Assert.Null(await Adapter(Catalog(BtcPair)).GetOrderBookAsync("BTC/USD", CancellationToken.None));
    }

    [Fact]
    public void The_specification_drops_every_field_that_moves()
    {
        // Measured on 2026-09-11: two catalogue fetches 45 s apart, all 120 pairs compared field by
        // field, and exactly ten paths moved. If any of them stayed in the spec, discovery would
        // mint a new instrument_spec version for every instrument on every pass, forever.
        var pair = JsonDocument.Parse(BtcPair).RootElement;
        var stripped = JsonNode.Parse(AvantisSpec.StaticJson(pair))!.AsObject();

        foreach (var moving in new[]
                 {
                     "openInterest", "coinOI", "pairOI", "fundingRate",
                     "accPerOiLong", "accPerOiShort", "fundingLastUpdateBlock", "decayedVol", "liquidity",
                 })
        {
            Assert.False(stripped.ContainsKey(moving), $"{moving} moves and must not be in the specification");
        }

        Assert.False(stripped["feed"]!.AsObject()["attributes"]!.AsObject().ContainsKey("isOpen"));
    }

    [Fact]
    public void The_specification_keeps_what_actually_specifies_the_instrument()
    {
        // The other side of the same rule: stripping too much would version away the facts the
        // table exists to hold. The schedule stays — it is a fact about the listing, and keeping it
        // is not the same as storing a calendar we would have to interpret.
        var stripped = JsonNode.Parse(AvantisSpec.StaticJson(JsonDocument.Parse(BtcPair).RootElement))!.AsObject();

        Assert.True(stripped.ContainsKey("leverages"));
        Assert.True(stripped.ContainsKey("pairMaxOI"));
        Assert.True(stripped.ContainsKey("minLevPosUSDC"));
        Assert.True(stripped["feed"]!.AsObject()["attributes"]!.AsObject().ContainsKey("schedule"));
    }

    [Fact]
    public void Two_readings_of_an_unchanged_pair_give_the_same_specification()
    {
        // The consequence that matters, stated directly: only the moving fields differ between two
        // catalogue fetches, so after stripping them the specification is byte-identical and
        // spec_hash does not move.
        var first = AvantisSpec.StaticJson(JsonDocument.Parse(BtcPair).RootElement);

        var moved = JsonNode.Parse(BtcPair)!.AsObject();
        moved["pairOI"] = 9_999_999.0;
        moved["decayedVol"] = 1.0;
        moved["openInterest"] = JsonNode.Parse("""{"long":1,"short":2}""");
        var second = AvantisSpec.StaticJson(JsonDocument.Parse(moved.ToJsonString()).RootElement);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_socket_diff_merges_into_the_catalogue_instead_of_replacing_it()
    {
        // The payloads are PARTIAL — a diff carries only the pairs that moved and only the fields
        // that moved within them. Replacing wholesale would blank everything the venue did not
        // happen to resend, which is most of the catalogue.
        var into = JsonNode.Parse(Catalog(BtcPair))!.AsObject();
        var diff = JsonNode.Parse("""{"pairInfos":{"0":{"pairOI":42}}}""")!.AsObject();

        AvantisCatalogFeed.Merge(into, diff);

        var pair = into["pairInfos"]!.AsObject()["0"]!.AsObject();
        Assert.Equal(42, pair["pairOI"]!.GetValue<double>());
        Assert.Equal("BTC", pair["from"]!.GetValue<string>());          // untouched
        Assert.NotNull(pair["pairParams"]);                            // untouched
    }

    [Fact]
    public void The_socket_address_is_the_protocols_and_not_a_choice() =>
        // Engine.IO 4 refuses anything else, and the scheme has to cross to ws.
        Assert.Equal(
            "wss://data.avantisfi.com/socket.io/?EIO=4&transport=websocket",
            AvantisCatalogFeed.SocketUrl("https://data.avantisfi.com"));

    [Fact]
    public async Task Discovery_marks_a_delisted_pair_rather_than_dropping_it()
    {
        var delisted = BtcPair.Replace("\"isPairListed\":true", "\"isPairListed\":false");
        var instruments = await Adapter(Catalog(delisted)).GetInstrumentsAsync(CancellationToken.None);

        Assert.Equal(InstrumentStatus.Delisted, Assert.Single(instruments).Status);
    }

    [Fact]
    public async Task An_instrument_carries_no_invented_price_step()
    {
        // The tick is the oracle's and the venue publishes no grid of its own. A fabricated step
        // would be read as the venue's own statement about its market.
        var i = Assert.Single(await Adapter(Catalog(BtcPair)).GetInstrumentsAsync(CancellationToken.None));

        Assert.Equal(0m, i.PriceStep);
        Assert.Null(i.FundingIntervalHours);
        Assert.Equal("BTC/USD", i.ExchangeSymbol);
    }

    private static AvantisMarketData Adapter(string catalog) =>
        new(new AvantisClient(new HttpClient(new StubHandler(catalog)), "https://data.test"));

    private sealed class StubHandler(string catalog, string? vault = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/v2/trading", StringComparison.Ordinal) ? catalog
                : path.EndsWith("/v2/lp/state", StringComparison.Ordinal) ? vault
                : null;

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }
}

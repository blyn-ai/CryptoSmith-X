using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace CryptoSmithX.MarketData.Api.Tests;

/// <summary>
/// The endpoints over real HTTP: routing, parameter binding and the validation contract, driven
/// through Kestrel rather than by calling handlers directly. That is what catches the mistakes unit
/// tests cannot see — a route spelled one way and requested another, a query parameter that never
/// binds because its name differs by a letter, a guard that runs after the database instead of
/// before it.
///
/// NO DATABASE, and none is needed for what is asserted here: every one of these requests is
/// refused before its handler reaches <c>db.OpenAsync</c>. The connection string below points
/// nowhere on purpose — if a guard ever regresses and lets a bad request through to Postgres, these
/// tests fail with a connection error instead of quietly passing, which is the failure we want.
///
/// The 200 paths need real rows and are verified against the deployed test environment, the same way
/// every other data path in this repository is.
/// </summary>
public sealed class EndpointContractTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new Db("Host=127.0.0.1;Port=1;Database=unreachable;Timeout=1"));

        _app = builder.Build();
        _app.MapMarketDataApi();
        _app.MapHistoryApi();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Every historical endpoint, by the exact path it is published at. A typo here is a
    /// 404 in production, so the list is spelled out rather than generated.</summary>
    public static TheoryData<string> HistoryPaths =>
    [
        "/v1/tickers/history",
        "/v1/funding",
        "/v1/open-interest",
        "/v1/depth",
        "/v1/trades",
        "/v1/liquidations",
        "/v1/liquidations/volume",
        "/v1/order-book-snapshots",
    ];

    private const string Window = "from=2026-09-09T00:00:00Z&to=2026-09-09T06:00:00Z";

    [Theory]
    [MemberData(nameof(HistoryPaths))]
    public async Task Every_history_endpoint_is_routed(string path)
    {
        // A bare request is refused for a missing parameter, not for a missing route. 404 here would
        // mean the endpoint was never mapped.
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(HistoryPaths))]
    public async Task Every_history_endpoint_requires_an_exchange(string path)
    {
        var response = await _client.GetAsync($"{path}?symbols=PF_XBTUSD&{Window}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("exchange", await Message(response), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(HistoryPaths))]
    public async Task Every_history_endpoint_requires_symbols(string path)
    {
        var response = await _client.GetAsync($"{path}?exchange=kraken-futures&{Window}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("symbols", await Message(response), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(HistoryPaths))]
    public async Task Every_history_endpoint_requires_a_window(string path)
    {
        var response = await _client.GetAsync($"{path}?exchange=kraken-futures&symbols=PF_XBTUSD");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("from and to", await Message(response));
    }

    /// <summary>The 48-hour rule, over HTTP, on every endpoint that has it — the one limit a caller
    /// is most likely to meet.</summary>
    [Theory]
    [MemberData(nameof(HistoryPaths))]
    public async Task Every_history_endpoint_caps_the_window_at_forty_eight_hours(string path)
    {
        var response = await _client.GetAsync(
            $"{path}?exchange=kraken-futures&symbols=PF_XBTUSD"
            + "&from=2026-09-01T00:00:00Z&to=2026-09-05T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("48", await Message(response));
    }

    [Theory]
    [MemberData(nameof(HistoryPaths))]
    public async Task Every_history_endpoint_refuses_more_than_fifty_symbols(string path)
    {
        var many = string.Join(',', Enumerable.Range(0, 51).Select(i => $"S{i}"));

        var response = await _client.GetAsync($"{path}?exchange=kraken-futures&symbols={many}&{Window}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("50", await Message(response));
    }

    [Theory]
    [MemberData(nameof(HistoryPaths))]
    public async Task Every_history_endpoint_refuses_a_cursor_it_did_not_issue(string path)
    {
        var response = await _client.GetAsync(
            $"{path}?exchange=kraken-futures&symbols=PF_XBTUSD&{Window}&cursor=obviously-not-ours");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cursor", await Message(response), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cursor this API issued must be accepted. It gets past validation and fails at the
    /// unreachable database, which is the proof that the guard let it through.</summary>
    [Fact]
    public async Task A_cursor_this_api_issued_passes_validation()
    {
        var ours = new Cursor(DateTimeOffset.Parse("2026-09-09T01:00:00Z"), "PF_XBTUSD", "").Encode();

        var response = await _client.GetAsync(
            $"/v1/trades?exchange=kraken-futures&symbols=PF_XBTUSD&{Window}&cursor={ours}");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Depth_refuses_a_band_that_was_never_measured()
    {
        var response = await _client.GetAsync(
            $"/v1/depth?exchange=kraken-futures&symbols=PF_XBTUSD&{Window}&bandsBps=10,33");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = await Message(response);
        Assert.Contains("33", message);
        Assert.Contains("10, 25, 50", message);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("10,25")]
    [InlineData("50,10,25")]
    public async Task Depth_accepts_the_bands_that_were_measured(string bands)
    {
        var response = await _client.GetAsync(
            $"/v1/depth?exchange=kraken-futures&symbols=PF_XBTUSD&{Window}&bandsBps={bands}");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- /v1/candles: the endpoint that already had a published contract ----

    /// <summary>
    /// The original call shape. It must never require the parameters the new endpoints require: a
    /// caller passing only symbol/tf/limit was valid before this work and stays valid after it.
    /// Reaching the database is the proof it passed every guard.
    /// </summary>
    [Fact]
    public async Task The_original_candles_call_still_passes_validation()
    {
        var response = await _client.GetAsync(
            "/v1/candles?exchange=kraken-futures&symbol=PF_XBTUSD&tf=1&limit=10");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Candles_still_refuses_a_non_positive_timeframe()
    {
        var response = await _client.GetAsync(
            "/v1/candles?exchange=kraken-futures&symbol=PF_XBTUSD&tf=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("tf", await Message(response));
    }

    [Fact]
    public async Task Candles_accepts_the_plural_symbols()
    {
        var response = await _client.GetAsync(
            "/v1/candles?exchange=kraken-futures&symbols=PF_XBTUSD,PF_ETHUSD&tf=15");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Half a window is a typo, and answering it by ignoring the half that was given would
    /// return a different range than the caller asked for.</summary>
    [Theory]
    [InlineData("from=2026-09-09T00:00:00Z")]
    [InlineData("to=2026-09-09T06:00:00Z")]
    public async Task Candles_refuses_half_a_window(string half)
    {
        var response = await _client.GetAsync(
            $"/v1/candles?exchange=kraken-futures&symbol=PF_XBTUSD&tf=1&{half}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("together", await Message(response));
    }

    [Fact]
    public async Task Candles_caps_an_explicit_window_at_forty_eight_hours()
    {
        var response = await _client.GetAsync(
            "/v1/candles?exchange=kraken-futures&symbol=PF_XBTUSD&tf=1"
            + "&from=2026-09-01T00:00:00Z&to=2026-09-05T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("48", await Message(response));
    }

    [Theory]
    [InlineData("trade")]
    [InlineData("mark")]
    [InlineData("index")]
    public async Task Candles_accepts_every_stored_price_type(string priceType)
    {
        var response = await _client.GetAsync(
            $"/v1/candles?exchange=kraken-futures&symbol=PF_XBTUSD&tf=1&priceType={priceType}");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Candles_refuses_a_price_type_that_is_not_stored()
    {
        var response = await _client.GetAsync(
            "/v1/candles?exchange=kraken-futures&symbol=PF_XBTUSD&tf=1&priceType=oracle");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("priceType", await Message(response));
    }

    /// <summary>The six original endpoints are still mapped. They are the published surface, and a
    /// route lost while adding new ones is the regression this whole exercise must not cause.</summary>
    [Theory]
    [InlineData("/v1/health")]
    [InlineData("/v1/exchanges")]
    [InlineData("/v1/instruments")]
    [InlineData("/v1/snapshot")]
    [InlineData("/v1/as-of")]
    [InlineData("/v1/coverage")]
    public async Task The_original_endpoints_are_still_routed(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<string> Message(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("error").GetString() ?? "";
    }
}

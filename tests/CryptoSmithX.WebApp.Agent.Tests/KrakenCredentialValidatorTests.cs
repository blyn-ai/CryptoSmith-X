using System.Net;
using System.Text;
using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Tests;

public sealed class KrakenCredentialValidatorTests
{
    [Fact]
    public async Task Futures_validator_returns_the_access_levels_of_a_valid_key()
    {
        var handler = new RecordingHandler(
            """{"result":"success","accounts":{}}""",
            """{"permissions":{"general":"FULL_ACCESS","transfer":"NO_ACCESS"}}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://futures.kraken.com") };
        var validator = new KrakenFuturesCredentialValidator(http);

        var result = await validator.ValidateAsync("public-key", Secret(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(["/derivatives/api/v3/accounts", "/api/auth/v1/api-keys/v3/check"], handler.Paths);
        Assert.Equal("public-key", handler.ApiKey);
        Assert.NotNull(handler.Authentication);
        Assert.Equal("Full access", result.Permissions!.Groups[0].Permissions[0].Access.Label);
        Assert.Equal("No access", result.Permissions.Groups[1].Permissions[0].Access.Label);
    }

    [Fact]
    public async Task Spot_validator_returns_the_individual_permissions_of_a_valid_key()
    {
        var handler = new RecordingHandler("""{"error":[],"result":{"permissions":["query-funds","withdraw-funds","modify-trades"]}}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.com") };
        var validator = new KrakenSpotCredentialValidator(http);

        var result = await validator.ValidateAsync("public-key", Secret(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(["/0/private/GetApiKeyInfo"], handler.Paths);
        Assert.Equal("public-key", handler.ApiKey);
        Assert.NotNull(handler.Authentication);
        Assert.Equal("Allowed", result.Permissions!.Groups[0].Permissions[0].Access.Label);
        Assert.Equal("Not allowed", result.Permissions.Groups[1].Permissions[0].Access.Label);
        Assert.Equal("Allowed", result.Permissions.Groups[1].Permissions[2].Access.Label);
    }

    [Fact]
    public async Task Invalid_secret_is_refused_without_a_network_request()
    {
        var handler = new RecordingHandler("{}" );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://futures.kraken.com") };
        var validator = new KrakenFuturesCredentialValidator(http);

        var result = await validator.ValidateAsync("public-key", "not base64!", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Empty(handler.Paths);
    }

    private static string Secret() => Convert.ToBase64String(Encoding.UTF8.GetBytes("test-secret"));

    private sealed class RecordingHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _responseIndex;

        public List<string> Paths { get; } = [];

        public string? ApiKey { get; private set; }

        public string? Authentication { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            ApiKey = request.Headers.TryGetValues("APIKey", out var futuresKey)
                ? futuresKey.Single()
                : request.Headers.TryGetValues("API-Key", out var spotKey)
                    ? spotKey.Single()
                    : null;
            Authentication = request.Headers.TryGetValues("Authent", out var futuresSignature)
                ? futuresSignature.Single()
                : request.Headers.TryGetValues("API-Sign", out var spotSignature)
                    ? spotSignature.Single()
                    : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodies[Math.Min(_responseIndex++, bodies.Length - 1)], Encoding.UTF8, "application/json"),
            });
        }
    }
}

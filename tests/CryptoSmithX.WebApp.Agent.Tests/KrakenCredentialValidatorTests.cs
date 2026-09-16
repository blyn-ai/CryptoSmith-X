using System.Net;
using System.Text;
using CryptoSmithX.WebApp.Agent.Data;

namespace CryptoSmithX.WebApp.Agent.Tests;

public sealed class KrakenCredentialValidatorTests
{
    [Fact]
    public async Task Futures_validator_accepts_a_successful_accounts_response()
    {
        var handler = new RecordingHandler("""{"result":"success","accounts":{}}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://futures.kraken.com") };
        var validator = new KrakenFuturesCredentialValidator(http);

        var result = await validator.ValidateAsync("public-key", Secret(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("/derivatives/api/v3/accounts", handler.Path);
        Assert.Equal("public-key", handler.ApiKey);
        Assert.NotNull(handler.Authentication);
    }

    [Fact]
    public async Task Spot_validator_accepts_a_response_without_errors()
    {
        var handler = new RecordingHandler("""{"error":[],"result":{}}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.com") };
        var validator = new KrakenSpotCredentialValidator(http);

        var result = await validator.ValidateAsync("public-key", Secret(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("/0/private/Balance", handler.Path);
        Assert.Equal("public-key", handler.ApiKey);
        Assert.NotNull(handler.Authentication);
    }

    [Fact]
    public async Task Invalid_secret_is_refused_without_a_network_request()
    {
        var handler = new RecordingHandler("{}" );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://futures.kraken.com") };
        var validator = new KrakenFuturesCredentialValidator(http);

        var result = await validator.ValidateAsync("public-key", "not base64!", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(handler.Path);
    }

    private static string Secret() => Convert.ToBase64String(Encoding.UTF8.GetBytes("test-secret"));

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string? ApiKey { get; private set; }

        public string? Authentication { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
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
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

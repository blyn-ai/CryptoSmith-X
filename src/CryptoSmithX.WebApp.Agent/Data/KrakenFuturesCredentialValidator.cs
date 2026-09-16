using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// Verifies a submitted Kraken Futures key with the harmless accounts endpoint. It never sends an
/// order and intentionally exposes no upstream response body to the page or logs.
/// </summary>
public sealed class KrakenFuturesCredentialValidator(HttpClient httpClient)
{
    public async Task<KrakenCredentialValidation> ValidateAsync(
        string? apiKey,
        string? apiSecret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return KrakenCredentialValidation.Invalid("Įvesk viešąjį ir privatųjį API raktą.");
        }

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(apiSecret.Trim());
        }
        catch (FormatException)
        {
            return KrakenCredentialValidation.Invalid("Privatus API raktas nėra tinkamo Kraken formato.");
        }

        const string requestPath = "/derivatives/api/v3/accounts";
        const string signingPath = "/api/v3/accounts";
        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var signature = Sign(signingPath, nonce, secret);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.Add("APIKey", apiKey.Trim());
        request.Headers.Add("Nonce", nonce);
        request.Headers.Add("Authent", signature);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return KrakenCredentialValidation.Invalid("Kraken nepatvirtino rakto. Patikrink raktus ir jų teises.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var succeeded = payload.RootElement.TryGetProperty("result", out var result)
                && string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase);
            return succeeded
                ? KrakenCredentialValidation.Valid
                : KrakenCredentialValidation.Invalid("Kraken nepatvirtino rakto. Patikrink raktus ir jų teises.");
        }
        catch (HttpRequestException)
        {
            return KrakenCredentialValidation.Invalid("Kraken dabar nepasiekiamas. Pabandyk dar kartą.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KrakenCredentialValidation.Invalid("Kraken atsakymas užtruko per ilgai. Pabandyk dar kartą.");
        }
        catch (JsonException)
        {
            return KrakenCredentialValidation.Invalid("Kraken atsiuntė netinkamą atsakymą. Pabandyk dar kartą.");
        }
    }

    private static string Sign(string signingPath, string nonce, byte[] secret)
    {
        var payload = Encoding.UTF8.GetBytes(nonce + signingPath);
        var hash = SHA256.HashData(payload);
        using var hmac = new HMACSHA512(secret);
        return Convert.ToBase64String(hmac.ComputeHash(hash));
    }
}

public sealed record KrakenCredentialValidation(bool IsValid, string? Message)
{
    public static KrakenCredentialValidation Valid { get; } = new(true, null);

    public static KrakenCredentialValidation Invalid(string message) => new(false, message);
}

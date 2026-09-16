using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>Checks a Kraken Spot key through Balance, which cannot place or alter any order.</summary>
public sealed class KrakenSpotCredentialValidator(HttpClient httpClient)
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

        const string path = "/0/private/Balance";
        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var postData = $"nonce={Uri.EscapeDataString(nonce)}";
        var signature = Sign(path, nonce, postData, secret);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("API-Key", apiKey.Trim());
        request.Headers.Add("API-Sign", signature);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return KrakenCredentialValidation.Invalid("Kraken nepatvirtino rakto. Patikrink raktus ir jų teises.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var hasErrors = payload.RootElement.TryGetProperty("error", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0;
            return hasErrors
                ? KrakenCredentialValidation.Invalid("Kraken nepatvirtino rakto. Patikrink raktus ir jų teises.")
                : KrakenCredentialValidation.Valid;
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

    private static string Sign(string path, string nonce, string postData, byte[] secret)
    {
        var nonceAndPostData = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + postData));
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var payload = new byte[pathBytes.Length + nonceAndPostData.Length];
        Buffer.BlockCopy(pathBytes, 0, payload, 0, pathBytes.Length);
        Buffer.BlockCopy(nonceAndPostData, 0, payload, pathBytes.Length, nonceAndPostData.Length);
        using var hmac = new HMACSHA512(secret);
        return Convert.ToBase64String(hmac.ComputeHash(payload));
    }
}

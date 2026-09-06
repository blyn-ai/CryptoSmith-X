using System.Text.Json;

namespace CryptoSmithX.WebApp.Admin.Data;

/// <summary>V1 policy validation: it only has to parse as JSON. Typed forms are a later task.</summary>
public static class PolicyJson
{
    public static bool TryValidate(string? json, out string normalised)
    {
        normalised = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            normalised = json;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

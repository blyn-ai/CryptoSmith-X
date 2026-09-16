namespace CryptoSmithX.WebApp.Agent.Models;

/// <summary>Only accepts a newly entered key pair. Existing secrets are never posted back to a page.</summary>
public sealed class KrakenCredentialRequest
{
    public string? Scope { get; init; }

    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }
}

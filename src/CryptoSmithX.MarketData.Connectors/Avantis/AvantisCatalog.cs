using System.Text.Json;

namespace CryptoSmithX.MarketData.Connectors.Avantis;

/// <summary>
/// One reading of the venue's catalogue, held as the JSON that arrived and parsed on demand.
///
/// The text is kept rather than only the typed view because the two callers want different things:
/// the ticker path wants figures, and discovery wants the pair's own payload to version as its
/// specification (with the observations cut out — see <see cref="AvantisSpec"/>). Re-fetching to
/// serve the second would be a second call for a response we already have.
/// </summary>
public sealed class AvantisCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lazy<AvTrading?> _typed;
    private readonly Lazy<JsonDocument?> _document;

    public AvantisCatalog(string json, DateTimeOffset at)
    {
        Json_ = json;
        At = at;
        _typed = new Lazy<AvTrading?>(() => JsonSerializer.Deserialize<AvTrading>(Json_, Json));
        _document = new Lazy<JsonDocument?>(() => JsonDocument.Parse(Json_));
    }

    /// <summary>When this copy was received. The socket sends partial diffs and no timestamps of
    /// its own, so this is OUR receive time and is never presented as the venue's.</summary>
    public DateTimeOffset At { get; }

    private string Json_ { get; }

    internal AvTrading? Typed => _typed.Value;

    /// <summary>Every pair's own JSON, keyed by the venue's <c>from/to</c> spelling — what
    /// discovery versions as the instrument's specification.</summary>
    internal IEnumerable<(string Symbol, JsonElement Raw)> RawPairs()
    {
        var doc = _document.Value;
        if (doc is null || !doc.RootElement.TryGetProperty("pairInfos", out var pairs)
            || pairs.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var entry in pairs.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object
                || !entry.Value.TryGetProperty("from", out var from)
                || !entry.Value.TryGetProperty("to", out var to))
            {
                continue;
            }

            var symbol = $"{from.GetString()}/{to.GetString()}";
            if (symbol.Length > 1)
            {
                yield return (symbol, entry.Value);
            }
        }
    }
}

using CryptoSmithX.Exchanges.Market;

namespace CryptoSmithX.Exchanges;

/// <summary>
/// Everything the hub needs from a venue. Public endpoints only — this service holds no keys and
/// places no orders. Implementations translate the wire format into the canonical records and
/// normalise units to what the DDL documents.
/// </summary>
public interface IExchangeMarketData
{
    /// <summary>Matches <c>exchange.code</c>.</summary>
    string ExchangeCode { get; }

    /// <summary>Linear perpetuals with a USD-family quote. Dated and inverse contracts are skipped.</summary>
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct);

    /// <summary>Every instrument in one call where the venue allows it.</summary>
    Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct);

    /// <summary>Closed 1-minute bars only; a bar still forming is never returned.</summary>
    Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
}

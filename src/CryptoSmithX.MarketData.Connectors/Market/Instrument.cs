namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// One listing on one exchange. Mirrors the columns of <c>exchange_instrument</c> that
/// discovery owns; identity, timestamps and status transitions belong to the store.
/// </summary>
/// <param name="ExchangeSymbol">Symbol exactly as the venue spells it.</param>
/// <param name="BaseAsset">Normalised by the adapter: XBT to BTC, 1000PEPE to PEPE.</param>
/// <param name="QuoteAsset">USD, USDT or USDC — V1 scope is the USD family.</param>
/// <param name="ContractMultiplier">Units of base asset per unit of quantity (1000PEPE: 1000).</param>
/// <param name="MinNotional">Null where the venue does not define one (Kraken).</param>
/// <param name="Status">One of the values allowed by the CHECK on exchange_instrument.status.</param>
/// <param name="RawJson">The venue's payload for this instrument, as received.</param>
public sealed record Instrument(
    string ExchangeSymbol,
    string BaseAsset,
    string QuoteAsset,
    decimal ContractMultiplier,
    decimal PriceStep,
    decimal QtyStep,
    decimal MinQty,
    decimal? MinNotional,
    short FundingIntervalHours,
    InstrumentStatus Status,
    string RawJson);

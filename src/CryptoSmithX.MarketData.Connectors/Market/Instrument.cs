namespace CryptoSmithX.MarketData.Connectors.Market;

/// <summary>
/// One listing on one exchange, as the venue reports it. The adapter does no normalisation:
/// it hands back the raw base/quote strings and the venue's own multiplier, and the Hub resolves
/// them to canonical assets against the <c>asset_alias</c> table. Identity, timestamps and status
/// transitions belong to the store.
/// </summary>
/// <param name="ExchangeSymbol">Symbol exactly as the venue spells it.</param>
/// <param name="BaseAssetRaw">Base asset exactly as the venue spells it (XBT, 1000PEPE) — not normalised.</param>
/// <param name="QuoteAssetRaw">Quote asset exactly as the venue spells it. V1 scope is the USD family.</param>
/// <param name="ContractMultiplier">
/// Units of base asset per unit of quantity, as the venue defines it. An alias multiplier
/// (1000PEPE to PEPE) is multiplied onto this by the Hub, not by the adapter.
/// </param>
/// <param name="MinNotional">Null where the venue does not define one (Kraken).</param>
/// <param name="ListedAt">When the contract listed on the venue (Kraken: openingDate); null if unknown.</param>
/// <param name="Status">One of the values allowed by the CHECK on exchange_instrument.status.</param>
/// <param name="RawJson">The venue's payload for this instrument, as received.</param>
public sealed record Instrument(
    string ExchangeSymbol,
    string BaseAssetRaw,
    string QuoteAssetRaw,
    decimal ContractMultiplier,
    decimal PriceStep,
    decimal QtyStep,
    decimal MinQty,
    decimal? MinNotional,
    short FundingIntervalHours,
    DateTimeOffset? ListedAt,
    InstrumentStatus Status,
    string RawJson);

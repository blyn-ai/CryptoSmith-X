namespace CryptoSmithX.WebApp.Studio.Models;

/// <summary>
/// Band 1's listing-cell disclosure (plan item B2): the instrument and its spec, opened in place
/// under the row. Everything band 1 shows is a MEASUREMENT — a price, a size, an age; this is the
/// CONTRACT those measurements are read against, and it changes on its own clock (<c>valid_from</c>),
/// not on the ticker's.
/// </summary>
/// <param name="BaseAssetRaw">The venue's own spelling of the base leg — the string that is the
/// INPUT to alias resolution, before <c>asset_alias</c> and <c>contract_multiplier</c> turn it into
/// the canonical asset this page already prints everywhere else.</param>
/// <param name="ListedAt">When the contract listed ON THE VENUE, by the venue's own account (Kraken:
/// <c>openingDate</c>) — null where the venue does not say. Distinct from <see cref="FirstSeenAt"/>,
/// which is when discovery first saw it; the gap between the two is the whole reason both are
/// printed rather than one standing in for the other.</param>
public sealed record InstrumentIdentity(
    string BaseAssetRaw,
    string QuoteAssetRaw,
    double ContractMultiplier,
    DateTime? ListedAt,
    DateTime FirstSeenAt);

public sealed record InstrumentLimits(
    double PriceStep,
    double QtyStep,
    double MinQty,
    double? MinNotional);

/// <param name="ChangedAt">Null on a listing nobody ever had to decide — <c>collect</c> is written
/// once, at INSERT (gate 0029), and a discovery pass never revisits it.</param>
public sealed record InstrumentStatus(
    string Status,
    DateTime StatusChangedAt,
    bool Collect,
    string? CollectNote,
    DateTime? ChangedAt,
    string? ChangedBy);

/// <summary>
/// The currently OPEN version of <c>instrument_spec</c> (<c>valid_to is null</c>) — null when
/// discovery has never written one for this instrument, which is a real, printable state and not an
/// error.
/// </summary>
/// <param name="ValidTo">Always null for the open version fetched here — kept on the record anyway
/// so the view's own sentence ("NULL = in effect") reads as a statement about the column, not a
/// hardcoded fact about this query.</param>
/// <param name="VenueEffectiveAt">When the venue actually changed the contract, if that is recorded
/// anywhere a human could read it and enter by hand — null means "not known", never "unchanged".</param>
/// <param name="FundingIntervalSource">venue / measured / assumed, or null when no source was ever
/// recorded for this version.</param>
public sealed record InstrumentSpecVersion(
    DateTime ValidFrom,
    DateTime? ValidTo,
    DateTime LastSeenAt,
    DateTime? VenueEffectiveAt,
    string SpecHash,
    string WrittenBy,
    string? FundingIntervalSource,
    string RawJson);

public sealed record InstrumentDetail(
    InstrumentIdentity Identity,
    InstrumentLimits Limits,
    InstrumentStatus Status,
    InstrumentSpecVersion? Spec);

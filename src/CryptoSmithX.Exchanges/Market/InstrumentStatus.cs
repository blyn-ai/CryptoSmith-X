namespace CryptoSmithX.Exchanges.Market;

/// <summary>
/// The states allowed by the CHECK on <c>exchange_instrument.status</c>.
/// <see cref="Delisted"/> is never reported by an adapter — the store assigns it when an
/// instrument has been missing from discovery for several consecutive polls.
/// </summary>
public enum InstrumentStatus
{
    Trading,
    PostOnly,
    ReduceOnly,
    Halted,
    Delisted,
}

public static class InstrumentStatusText
{
    public static string ToDb(this InstrumentStatus status) => status switch
    {
        InstrumentStatus.Trading => "trading",
        InstrumentStatus.PostOnly => "post_only",
        InstrumentStatus.ReduceOnly => "reduce_only",
        InstrumentStatus.Halted => "halted",
        InstrumentStatus.Delisted => "delisted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

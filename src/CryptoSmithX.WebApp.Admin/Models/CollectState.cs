namespace CryptoSmithX.WebApp.Admin.Models;

/// <summary>
/// What the console shows in the collect column. Three values, because since 0029 there are three
/// situations and the page could previously only say two of them.
/// </summary>
public enum CollectState
{
    /// <summary>We are collecting this listing. Somebody, or the auto-approve list, said yes.</summary>
    On,

    /// <summary>Somebody looked at this listing and said no. The row carries who and when.</summary>
    Off,

    /// <summary>Nobody has decided. It arrived, the gate held it, and it is waiting for a human.</summary>
    New,
}

/// <summary>
/// The one place the derived NEW state is defined for the console.
///
/// 0029 chose not to store this state in a column of its own: a row nobody has decided is exactly
/// <c>collect = false and collect_changed_at is null</c>, because the only writer of those columns
/// is <see cref="Data.InstrumentStore.SaveCollectAsync"/>, which writes all four in one statement
/// and cannot set <c>collect</c> without stamping the clock. Discovery writes <c>collect</c> on the
/// insert path only and never touches the audit columns, so a listing it gates is undecided by
/// construction rather than merely switched off.
///
/// That derivation is repeated in exactly two places and nowhere else: here, for everything the
/// console renders, and as a SQL predicate in <see cref="Data.InstrumentStore.UndecidedSql"/> for
/// the filter and the queue count, which cannot call into C#. The two are kept honest by
/// CollectGateStateTests, which asserts the predicate names the same two columns with the same two
/// tests. A third copy — in a view, in a query, in a store — is the thing to reject on sight.
/// </summary>
public static class CollectGate
{
    public static CollectState State(bool collect, DateTime? collectChangedAt) =>
        collect ? CollectState.On
        : collectChangedAt is null ? CollectState.New
        : CollectState.Off;
}

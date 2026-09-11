namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// What counts as "the venue did not give us this number", at the one place it is written down.
///
/// Absence has three spellings and they all mean the same thing. <c>null</c> is the one an adapter
/// uses now. <c>NaN</c> is the older one, from when the snapshot columns were NOT NULL and a record
/// could not hold an absence at all — it is still how a couple of adapters say it, and Postgres
/// accepts NaN in a <c>double precision</c> column perfectly happily, so an unnormalised one would
/// be STORED: a value we never observed, sitting exactly where the absence belongs. Infinity is
/// what a division by a missing price leaves behind and is no more a measurement than the other two.
///
/// A separate class rather than a local function so the rule is reachable from a test. It replaced
/// a guard that dropped the whole observation, and a guard that is gone is exactly the kind of
/// thing that comes back wrong.
/// </summary>
internal static class Figures
{
    internal static bool Absent(double? v) => v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value);

    /// <summary>The figure, or NULL where there was never a measurement. Never a zero: a zero is an
    /// observation, and inventing one is the single thing this system must not do.</summary>
    internal static double? Num(double? v) => Absent(v) ? null : v;
}

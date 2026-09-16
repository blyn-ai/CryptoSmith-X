using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The instant the tape fixtures were captured around. <see cref="RestTape"/> keeps only prints from the
/// last two days (and up to an hour ahead) of "now", so a test that replays a captured tape against the
/// wall clock passes for two days and then fails for ever — which is what happened on 2026-09-16.
/// The captured tapes span 2026-09-12 20:08 (MEXC) to 2026-09-13 12:15 (Synthetix); 12:30 that day
/// covers all of them.
/// </summary>
internal static class FixtureClock
{
    public static readonly DateTimeOffset CapturedAround = new(2026, 9, 13, 12, 30, 0, TimeSpan.Zero);

    public static IDisposable Freeze() => RestTape.FreezeClock(CapturedAround);
}

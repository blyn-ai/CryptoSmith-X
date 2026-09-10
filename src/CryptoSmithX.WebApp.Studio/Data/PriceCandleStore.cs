using System.Data.Common;
using CryptoSmithX.WebApp.Studio.Models;
using Dapper;

namespace CryptoSmithX.WebApp.Studio.Data;

/// <summary>
/// B3's series selector: mark and index candles, from <c>market_price_candle</c> — a table 0032
/// created, granted to <c>studio_reader</c>, and left completely unread until this. Only Binance
/// writes either series (0041); every other venue's rows are simply absent, which reads through this
/// store exactly like a venue with no bars, and the view says so rather than leaving a blank panel.
/// </summary>
public static class PriceCandleStore
{
    /// <summary>
    /// No <c>bar_count</c> on this table — see 0032's own comment, "объём не заводится: mark/index
    /// не торгуются напрямую" reads the same way about partial-bar tracking: there is no 1m rollup
    /// step here to be partial, a row is written whole or not written. <c>BarCount</c> is set to the
    /// timeframe itself so <see cref="CandleSeries.Partial"/> reads zero rather than reading a
    /// coverage signal this table does not keep.
    /// </summary>
    public const string Sql =
        """
        select c.exchange_instrument_id as "InstrumentId",
               c.open_time              as "OpenTime",
               c.open                   as "Open",
               c.high                   as "High",
               c.low                    as "Low",
               c.close                  as "Close",
               c.source                 as "Source",
               c.received_at            as "UpdatedAt"
          from market_price_candle c
         where c.exchange_instrument_id = any(@instrumentIds)
           and c.series = @series
           and c.timeframe = @timeframe
           and c.open_time >= @from
           and c.open_time <= @anchor
         order by c.exchange_instrument_id, c.open_time
        """;

    public static async Task<IReadOnlyDictionary<int, CandleSeries>> ReadAsync(
        DbConnection conn, IReadOnlyList<int> instrumentIds, DateTimeOffset now,
        short timeframeMinutes, int count, string series, CancellationToken ct)
    {
        if (instrumentIds.Count == 0)
        {
            return new Dictionary<int, CandleSeries>();
        }

        var windows = CandleStore.Windows(now, timeframeMinutes, count);
        var anchor = windows[^1];

        var rows = (await conn.QueryAsync<(int InstrumentId, DateTime OpenTime, double Open,
                double High, double Low, double Close, string? Source, DateTime UpdatedAt)>(
            new CommandDefinition(
                Sql,
                new
                {
                    instrumentIds = instrumentIds.ToArray(),
                    series,
                    timeframe = (int)timeframeMinutes,
                    from = windows[0],
                    anchor
                },
                cancellationToken: ct))).ToList();

        var byInstrument = rows
            .GroupBy(r => r.InstrumentId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.OpenTime));

        return instrumentIds.Distinct().ToDictionary(
            id => id,
            id =>
            {
                byInstrument.TryGetValue(id, out var byTime);
                var bars = windows
                    .Select(w => byTime is not null && byTime.TryGetValue(w, out var r)
                        ? new CandleRow(r.InstrumentId, r.OpenTime, r.Open, r.High, r.Low, r.Close,
                            timeframeMinutes, r.Source, r.UpdatedAt)
                        : null)
                    .ToList();
                return new CandleSeries(windows, bars, timeframeMinutes);
            });
    }
}

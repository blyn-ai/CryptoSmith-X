using System.Data.Common;
using CryptoSmithX.Arena.Models;
using Dapper;

namespace CryptoSmithX.Arena.Data;

/// <summary>
/// The hourly price history behind the pair page: the candle panels under the table, and the
/// sparklines on bid, ask and last inside it.
///
/// <b>Why one query serves both.</b> Rule 11 of the design system was corrected against the
/// rendered page (see the Corrections section of the system's readme): a perpetual row carries
/// seven sparklines, not five, because the price series feeds three columns rather than one — bid,
/// ask and last are all drawn from the same hourly closes. So the line under bid is not a history
/// of bid; it is the price history, shown three times because the three figures sit on it. Fetching
/// it three times, or storing it three times, would be three names for one series.
///
/// <b>Why hourly.</b> `market_metric_hour` is the hourly grain the rest of the surface is written
/// against, and the candle panels are labelled "hourly · 24 h". Reading 1m bars and rolling them up
/// here would be re-implementing <c>RollupJob</c> in a view layer, against a table that already
/// holds the answer.
///
/// <b>Where rule 11's other four series come from.</b> Spread, funding, open interest and depth
/// 25bps live in <c>market_metric_hour</c> and are read by <see cref="MetricHourStore"/>, which
/// shares this file's window list so all seven lines on a row sit on one axis. That table was
/// withheld from <c>arena_reader</c> by 0025 on a sentence that turned out to be false about the
/// page; 0026 grants it and argues the case at length.
/// </summary>
public static class CandleStore
{
    /// <summary>
    /// The timeframe the panels and the sparklines are drawn at, in minutes.
    ///
    /// Sixty because that is what the rollup keeps for long spans and what the page's own header
    /// claims. The rollup writes 1, 5, 15, 60, 240, 720 and 1440 (0001), so this is a choice among
    /// stored grains rather than a computation.
    /// </summary>
    public const short TimeframeMinutes = 60;

    /// <summary>
    /// How many hours of history the page shows. Twenty-five bars for twenty-four hours of history:
    /// the current hour is open and will move, so it is fetched as the twenty-fifth and the panel
    /// header still says 24 h of closed history behind it.
    /// </summary>
    public const int Hours = 25;

    /// <summary>
    /// Closed hourly bars for a set of instruments, newest window last.
    ///
    /// The window floor is an absolute instant computed by the caller and passed in, never
    /// <c>now()</c> in SQL: every instant this application prints is computed against the time of
    /// the REQUEST (blueprint §5), and a query that reads the database's clock would put a second
    /// clock on a page whose subject is that there is only ever one.
    ///
    /// Bars are NOT gap-filled here. A window a venue has no bar for is simply absent from the
    /// result, and the caller reserves its slot on the shared time axis; see
    /// <see cref="CandleSeries"/>.
    /// </summary>
    public const string Sql =
        """
        select c.exchange_instrument_id as "InstrumentId",
               c.open_time              as "OpenTime",
               c.open                   as "Open",
               c.high                   as "High",
               c.low                    as "Low",
               c.close                  as "Close",
               c.bar_count              as "BarCount"
          from market_candle c
         where c.exchange_instrument_id = any(@instrumentIds)
           and c.timeframe = @timeframe
           and c.open_time >= @from
           and c.open_time <= @anchor
         order by c.exchange_instrument_id, c.open_time
        """;

    /// <summary>
    /// One series per instrument asked for, every one of them on the SAME list of hourly windows.
    ///
    /// The window list is computed rather than read from the union of what exists, and this is the
    /// one thing in the file that has to be got right. Reading the union would silently close every
    /// gap: a venue that went dark for six hours would have its remaining bars slide together and
    /// draw as an unbroken line, which is the page telling the reader a venue was quoting when it
    /// was not. <c>PairStore.AtAsync</c> in the admin console computes its window list for exactly
    /// this reason, and this is the same argument on the public surface, where nobody is there to
    /// know better.
    ///
    /// An instrument with no bars at all still gets a series — an empty one. It is a venue with no
    /// price history, which is a fact about the venue, and dropping it would leave the reader to
    /// infer from a missing panel something the page could simply say.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, CandleSeries>> ReadAsync(
        DbConnection conn, IReadOnlyList<int> instrumentIds, DateTimeOffset now, CancellationToken ct)
    {
        if (instrumentIds.Count == 0)
        {
            return new Dictionary<int, CandleSeries>();
        }

        var windows = Windows(now);
        var anchor = windows[^1];

        var rows = (await conn.QueryAsync<CandleRow>(new CommandDefinition(
            Sql,
            new
            {
                instrumentIds = instrumentIds.ToArray(),
                timeframe = (int)TimeframeMinutes,
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
                    .Select(w => byTime is not null && byTime.TryGetValue(w, out var r) ? r : null)
                    .ToList();
                return new CandleSeries(windows, bars);
            });
    }

    /// <summary>
    /// The hourly windows every series on the page is drawn on, oldest first, the last of them the
    /// most recently CLOSED hour.
    ///
    /// <b>One list, computed once, used by both stores.</b> The price line and the four lines from
    /// <see cref="MetricHourStore"/> sit in adjacent columns of the same row and are read across as
    /// if they shared a time axis. They only do if the axis is literally the same list — two
    /// implementations that agree today would drift the first time one of them was changed, and the
    /// drift would show as nothing at all: seven lines still drawn, still eleven pixels tall, no
    /// longer describing the same twenty-five hours.
    ///
    /// The anchor is the last CLOSED hour because the rollup only ever writes closed rows (0001,
    /// "Только закрытые бары"), so asking for the current hour asks for a row that will not exist
    /// for up to fifty-nine minutes — and an empty trailing slot on every series would read as
    /// every venue going quiet at the same instant.
    /// </summary>
    public static IReadOnlyList<DateTime> Windows(DateTimeOffset now)
    {
        var anchor = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)
            .AddHours(now.UtcDateTime.Hour)
            .AddHours(-1);

        return Enumerable.Range(0, Hours)
            .Select(i => anchor.AddHours(-(Hours - 1 - i)).UtcDateTime)
            .ToList();
    }
}

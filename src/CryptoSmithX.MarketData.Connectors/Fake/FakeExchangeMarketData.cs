using System.Globalization;
using CryptoSmithX.MarketData.Connectors.Market;

namespace CryptoSmithX.MarketData.Connectors.Fake;

/// <summary>
/// A venue that never leaves the process. Deterministic for a given seed: the same seed and the
/// same minute always produce the same price, so candles fetched twice agree and a test can assert
/// on exact numbers. Two symbols are deliberately awkward — one drops out of discovery from time to
/// time, one has holes in its minute series — so the delisting and bar_count paths get exercised
/// without waiting for a real venue to misbehave.
/// </summary>
public sealed class FakeExchangeMarketData : IExchangeMarketData
{
    /// <summary>The symbol that periodically vanishes from discovery.</summary>
    public const string FlakySymbol = "FAKE-ARB-USD";

    /// <summary>The symbol whose 1-minute series has holes.</summary>
    public const string GappySymbol = "FAKE-INJ-USD";

    private static readonly (string Base, double Price, decimal Multiplier)[] Catalogue =
    [
        ("BTC", 98_000, 1), ("ETH", 3_400, 1), ("SOL", 185, 1), ("XRP", 2.35, 1),
        ("DOGE", 0.32, 1), ("ADA", 0.95, 1), ("AVAX", 38, 1), ("LINK", 22, 1),
        ("DOT", 7.4, 1), ("LTC", 105, 1), ("BCH", 450, 1), ("ATOM", 6.8, 1),
        ("NEAR", 5.6, 1), ("APT", 11, 1), ("ARB", 0.82, 1), ("OP", 1.9, 1),
        ("INJ", 24, 1), ("SUI", 4.1, 1), ("TIA", 5.2, 1), ("PEPE", 0.0000178, 1000),
    ];

    private readonly int _seed;
    private readonly TimeProvider _clock;
    private int _discoveryCalls;

    public FakeExchangeMarketData(int seed = 20260826, TimeProvider? clock = null)
    {
        _seed = seed;
        _clock = clock ?? TimeProvider.System;
    }

    public string ExchangeCode => "fake";

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var round = Interlocked.Increment(ref _discoveryCalls);

        var list = new List<Instrument>(Catalogue.Length);
        foreach (var (baseAsset, price, multiplier) in Catalogue)
        {
            var symbol = Symbol(baseAsset);

            // Out of discovery on every third round: enough to trip the delisting counter and
            // come back, which is the interesting half of that path.
            if (symbol == FlakySymbol && round % 3 == 0)
            {
                continue;
            }

            var status = symbol switch
            {
                "FAKE-TIA-USD" => InstrumentStatus.PostOnly,
                "FAKE-SUI-USD" => InstrumentStatus.Halted,
                _ => InstrumentStatus.Trading,
            };

            list.Add(new Instrument(
                ExchangeSymbol: symbol,
                BaseAsset: baseAsset,
                QuoteAsset: "USD",
                ContractMultiplier: multiplier,
                PriceStep: StepFor(price),
                QtyStep: 0.001m,
                MinQty: 0.001m,
                MinNotional: baseAsset == "BTC" ? null : 5m,
                FundingIntervalHours: 8,
                Status: status,
                RawJson: RawJson(symbol, baseAsset, price, multiplier, status)));
        }

        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }

    public Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        var minute = MinuteIndex(now);

        var list = new List<Ticker>(Catalogue.Length);
        foreach (var (baseAsset, _, _) in Catalogue)
        {
            var symbol = Symbol(baseAsset);

            // Within the minute the price keeps moving, so two polls ten seconds apart differ
            // while still converging on the same close the candle will report.
            var progress = (now - FloorMinute(now)).TotalSeconds / 60d;
            var last = Lerp(PriceAt(symbol, minute), PriceAt(symbol, minute + 1), progress);

            var halfSpread = last * (0.00005 + 0.0004 * Unit(symbol, minute, 11));
            var bid = last - halfSpread;
            var ask = last + halfSpread;
            var mark = last * (1 + 0.00002 * (Unit(symbol, minute, 12) - 0.5));
            var index = last * (1 + 0.00003 * (Unit(symbol, minute, 13) - 0.5));

            var hasBook = Unit(symbol, minute, 14) > 0.1;
            var depth = hasBook
                ? new Depth(
                    Bid10Bps: Notional(last, minute, symbol, 10, 0),
                    Ask10Bps: Notional(last, minute, symbol, 10, 1),
                    Bid25Bps: Notional(last, minute, symbol, 25, 0),
                    Ask25Bps: Notional(last, minute, symbol, 25, 1),
                    // The 50 bps band is the one that is regularly not covered by the levels
                    // the venue returned, which is exactly when the column must stay null.
                    Bid50Bps: Unit(symbol, minute, 17) > 0.3 ? Notional(last, minute, symbol, 50, 0) : null,
                    Ask50Bps: Unit(symbol, minute, 18) > 0.3 ? Notional(last, minute, symbol, 50, 1) : null,
                    At: now.AddSeconds(-Unit(symbol, minute, 19) * 60))
                : null;

            list.Add(new Ticker(
                ExchangeSymbol: symbol,
                ReceivedAt: now,
                LastPrice: last,
                BidPrice: bid,
                AskPrice: ask,
                BidSize: 10 + 500 * Unit(symbol, minute, 20),
                AskSize: 10 + 500 * Unit(symbol, minute, 21),
                MarkPrice: mark,
                IndexPrice: index,
                FundingRate: (Unit(symbol, minute, 22) - 0.5) * 0.0002,
                Turnover24h: 1_000_000 + 90_000_000 * Unit(symbol, minute, 23),
                OpenInterest: 1_000 + 400_000 * Unit(symbol, minute, 24),
                OpenInterestAt: now.AddSeconds(-Unit(symbol, minute, 25) * 60),
                Depth: depth));
        }

        return Task.FromResult<IReadOnlyList<Ticker>>(list);
    }

    public Task<IReadOnlyList<Candle>> GetCandles1mAsync(
        string exchangeSymbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Closed bars only: the minute in progress is never returned.
        var lastClosed = MinuteIndex(_clock.GetUtcNow()) - 1;
        var first = MinuteIndex(from);
        var last = Math.Min(MinuteIndex(to), lastClosed);

        var list = new List<Candle>();
        for (var m = first; m <= last; m++)
        {
            // A venue that has no trades for a minute may simply never send that bar.
            if (exchangeSymbol == GappySymbol && Hash(exchangeSymbol, m, 31) % 5 == 0)
            {
                continue;
            }

            var open = PriceAt(exchangeSymbol, m);
            var close = PriceAt(exchangeSymbol, m + 1);
            var wickUp = 1 + 0.0015 * Unit(exchangeSymbol, m, 41);
            var wickDown = 1 - 0.0015 * Unit(exchangeSymbol, m, 42);

            list.Add(new Candle(
                ExchangeSymbol: exchangeSymbol,
                OpenTime: FromMinuteIndex(m),
                Open: open,
                High: Math.Max(open, close) * wickUp,
                Low: Math.Min(open, close) * wickDown,
                Close: close,
                Volume: 0.5 + 900 * Unit(exchangeSymbol, m, 43),
                // Half the catalogue behaves like Kraken Futures and WEEX: no trade counter.
                TradeCount: Hash(exchangeSymbol, 0, 44) % 2 == 0
                    ? null
                    : 1 + (int)(Hash(exchangeSymbol, m, 45) % 400)));
        }

        return Task.FromResult<IReadOnlyList<Candle>>(list);
    }

    private static string Symbol(string baseAsset) => $"FAKE-{baseAsset}-USD";

    private static DateTimeOffset FloorMinute(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

    private static long MinuteIndex(DateTimeOffset t) => t.ToUnixTimeSeconds() / 60;

    private static DateTimeOffset FromMinuteIndex(long minute) =>
        DateTimeOffset.FromUnixTimeSeconds(minute * 60);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static decimal StepFor(double price) => price switch
    {
        >= 10_000 => 0.5m,
        >= 100 => 0.01m,
        >= 1 => 0.0001m,
        >= 0.01 => 0.000001m,
        _ => 0.00000001m,
    };

    private double Notional(double price, long minute, string symbol, int bps, int side) =>
        price * (5_000 + 250_000 * Unit(symbol, minute, 50 + (bps * 2) + side)) * (bps / 10d);

    /// <summary>
    /// Price of a symbol at the start of a minute. Composed of two slow waves and a small noise
    /// term rather than an accumulating walk, so any minute can be evaluated on its own — that is
    /// what makes a refetch of an old window return the same bars.
    /// </summary>
    private double PriceAt(string symbol, long minute)
    {
        var baseAsset = symbol.Split('-')[1];
        var seed = Catalogue.First(c => c.Base == baseAsset).Price;
        var phase = Hash(symbol, 0, 7) % 1_000 / 159.0;

        var drift = 0.02 * Math.Sin((minute / 97.0) + phase);
        var swing = 0.008 * Math.Sin((minute / 31.0) + (phase * 2));
        var noise = 0.0025 * (Unit(symbol, minute, 3) - 0.5);
        return seed * (1 + drift + swing + noise);
    }

    /// <summary>Deterministic value in [0,1) from the seed, symbol, minute and a salt.</summary>
    private double Unit(string symbol, long minute, int salt) =>
        (Hash(symbol, minute, salt) % 1_000_000) / 1_000_000.0;

    private ulong Hash(string symbol, long minute, int salt)
    {
        // SplitMix64 over a cheap fold of the inputs — stable across runs and platforms, which
        // matters because the tests assert on the numbers this produces.
        unchecked
        {
            var x = (ulong)_seed * 0x9E3779B97F4A7C15UL;
            foreach (var ch in symbol)
            {
                x = ((x ^ ch) * 0x100000001B3UL) + 0x632BE59BD9B4E019UL;
            }

            x ^= (ulong)minute * 0xBF58476D1CE4E5B9UL;
            x ^= (ulong)salt * 0x94D049BB133111EBUL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    private static string RawJson(
        string symbol, string baseAsset, double price, decimal multiplier, InstrumentStatus status) =>
        string.Create(CultureInfo.InvariantCulture,
            $$"""{"symbol":"{{symbol}}","base":"{{baseAsset}}","quote":"USD","tick":{{price}},"mult":{{multiplier}},"state":"{{status.ToDb()}}"}""");
}

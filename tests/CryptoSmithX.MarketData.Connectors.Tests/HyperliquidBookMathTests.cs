using System.Text.Json;
using CryptoSmithX.MarketData.Connectors.Hyperliquid;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// <see cref="HyperliquidBookMath"/> turns one <c>l2Book</c> response into both the ticker's
/// top-of-book and the depth collector's cumulative notional — shared by the REST and WS feeds, so
/// this is where the numbers are pinned once rather than in each feed's own tests.
/// </summary>
public sealed class HyperliquidBookMathTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyperliquid");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Compute_reads_top_of_book_from_the_best_first_levels()
    {
        var book = Load("l2book_BTC.json");
        var (top, _) = HyperliquidBookMath.Compute(book, DateTimeOffset.UtcNow);

        Assert.NotNull(top);
        Assert.Equal(77117.0, top!.BidPrice);
        Assert.Equal(0.30094, top.BidSize);
        Assert.Equal(77118.0, top.AskPrice);
        Assert.Equal(6.50261, top.AskSize);
    }

    [Fact]
    public void Compute_gives_mixed_bounded_and_unbounded_bands_on_a_wider_book()
    {
        // Real OP book, 20 levels/side: bid span ~42 bps, ask span ~91 bps — wide enough that 10/25
        // bps bound on both sides, but 50 bps only bounds on the ask side. Expected sums computed
        // independently in Python from the same fixture (see the commit recon notes).
        var book = Load("l2book_op_wide.json");
        var (top, depth) = HyperliquidBookMath.Compute(book, DateTimeOffset.UtcNow);

        Assert.NotNull(top);
        Assert.NotNull(depth);
        Assert.Equal(8852.44, depth!.Bid10Bps!.Value, 1);
        Assert.Equal(15705.31, depth.Ask10Bps!.Value, 1);
        Assert.Equal(44642.90, depth.Bid25Bps!.Value, 1);
        Assert.Equal(41808.56, depth.Ask25Bps!.Value, 1);
        Assert.Null(depth.Bid50Bps);              // the fixture's bid side never reaches 50 bps
        Assert.Equal(94084.89, depth.Ask50Bps!.Value, 1);
    }

    [Fact]
    public void Compute_returns_null_top_and_depth_for_a_coin_with_no_market()
    {
        // A delisted/no-market coin serves levels as empty arrays, not null — confirmed live.
        var book = Load("l2book_nomarket.json");
        var (top, depth) = HyperliquidBookMath.Compute(book, DateTimeOffset.UtcNow);

        Assert.Null(top);
        Assert.Null(depth);
    }

    private static HlL2Book Load(string fixture) =>
        JsonSerializer.Deserialize<HlL2Book>(File.ReadAllText(Path.Combine(Dir, fixture)), Json)!;
}

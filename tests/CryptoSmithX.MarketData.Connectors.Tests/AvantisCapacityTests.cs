using CryptoSmithX.MarketData.Connectors.Avantis;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The venue's own availableLiquidity, ported. Its SDK says it mirrors <c>avantis-ui-v2
/// lib/trade.ts</c> — the same function the venue's trading screen uses to decide what it will let
/// anyone open — so what these tests defend is fidelity to somebody else's arithmetic, not a rule of
/// ours.
/// </summary>
public sealed class AvantisCapacityTests
{
    private static (double? Long, double? Short) Available(
        double? maxOi = 100_000_000, double? totalOi = 0,
        double? groupMax = 50_000_000, double? groupOi = 0,
        double? wallet = 10_000_000, double? groupPct = 100,
        double? longP = 100, double? shortP = 100,
        double? pairMax = 8_000_000, double? longOi = 0, double? shortOi = 0,
        double? buy = 9_000_000, double? sell = 9_000_000) =>
        AvantisCapacity.Available(
            maxOi, totalOi, groupMax, groupOi, wallet, groupPct, longP, shortP,
            pairMax, longOi, shortOi, buy, sell);

    [Fact]
    public void The_binding_constraint_is_the_smallest_ceiling_and_not_the_first_one_checked()
    {
        // Six caps apply at once. Reporting any but the smallest would tell a reader the venue will
        // take size it will in fact refuse.
        var (l, _) = Available(pairMax: 8_000_000, buy: 3_000_000);

        Assert.Equal(3_000_000d, l!.Value, 6);
    }

    [Fact]
    public void A_ceiling_the_venue_did_not_state_binds_nothing_rather_than_binding_everything()
    {
        // The hazard of folding nulls into a minimum: an unstated cap read as zero would make every
        // size column on this venue read zero, which looks exactly like a market that is full.
        var (l, _) = Available(wallet: null, groupMax: null, groupPct: null);

        Assert.Equal(8_000_000d, l!.Value, 6);
    }

    [Fact]
    public void A_venue_that_states_no_ceiling_at_all_reports_nothing_rather_than_zero()
    {
        var (l, s) = Available(
            maxOi: null, groupMax: null, wallet: null, groupPct: null,
            pairMax: null, buy: null, sell: null);

        Assert.Null(l);
        Assert.Null(s);
    }

    [Fact]
    public void Headroom_is_the_ceiling_less_what_is_already_used()
    {
        var (l, _) = Available(maxOi: 100_000_000, totalOi: 99_000_000);

        Assert.Equal(1_000_000d, l!.Value, 6);
    }

    [Fact]
    public void A_cap_already_exceeded_is_no_room_left_and_never_a_negative_size()
    {
        var (l, _) = Available(pairMax: 1_000_000, longOi: 900_000, shortOi: 900_000);

        Assert.Equal(0d, l!.Value, 6);
    }

    [Fact]
    public void The_two_sides_are_capped_independently_by_their_own_shares_and_their_own_use()
    {
        // The asymmetry that matters on a skewed market: the crowded side runs out first, and a
        // single "size" figure would hide exactly that.
        var (l, s) = Available(
            groupMax: 10_000_000, groupPct: 100, longP: 50, shortP: 50,
            longOi: 4_000_000, shortOi: 0, pairMax: 100_000_000, buy: null, sell: null);

        Assert.Equal(1_000_000d, l!.Value, 6);
        Assert.Equal(5_000_000d, s!.Value, 6);
    }

    [Fact]
    public void Buy_bounds_the_long_side_and_sell_the_short_one()
    {
        // Taken from the SDK's own parameter mapping rather than from what the words sound like: a
        // long is a buyer, so liquidity.buy is the ask side. Swapping these would put each figure
        // under the opposite column and still look entirely plausible.
        var (l, s) = Available(buy: 111_111, sell: 222_222);

        Assert.Equal(111_111d, l!.Value, 6);
        Assert.Equal(222_222d, s!.Value, 6);
    }
}

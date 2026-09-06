using CryptoSmithX.Studio.Data;
using CryptoSmithX.Studio.Models;

namespace CryptoSmithX.Studio.Tests;

/// <summary>
/// The loudest sentence on the page, and the counts beside it.
///
/// Both were the same class of fault: the page stating something it did not know. The sentence was
/// computed once on the server and never advanced, so a tab left open for three minutes read "Every
/// call is inside its window." in the largest type on the surface over cells that had all flipped to
/// "degraded". The counts were the ROW count printed three times under three different words, so a
/// reader was told four exchanges quote the pair when one exchange was in the table twice.
/// </summary>
public sealed class StatementTests
{
    // ── The sentence ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_quiet_page_says_so_rather_than_reaching_for_something_dramatic()
    {
        var rows = new[] { Rows.At(Rows.Venue(1)), Rows.At(Rows.Venue(2)) };
        Assert.Equal(Statement.InsideTheWindow, Statement.Verdict(rows));
    }

    [Fact]
    public void A_degraded_feed_outranks_a_late_call()
    {
        // Both are true at once here and only one of them can be the sentence. The worse fact wins:
        // a feed that has stopped meaning anything is not reported as a feed that is behind.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1), price: TimeSpan.FromDays(3).TotalSeconds),
            Rows.At(Rows.Venue(2), price: Rows.Window * 2)
        };

        Assert.Equal(Statement.OneFeedDegraded, Statement.Verdict(rows));
    }

    [Fact]
    public void Two_dead_feeds_are_counted_and_not_pluralised_by_hand()
    {
        var rows = new[]
        {
            Rows.At(Rows.Venue(1), price: TimeSpan.FromDays(3).TotalSeconds),
            Rows.At(Rows.Venue(2), depth: TimeSpan.FromDays(3).TotalSeconds),
            Rows.At(Rows.Venue(3))
        };

        Assert.Equal("2 feeds have stopped meaning anything.", Statement.Verdict(rows));
    }

    [Fact]
    public void A_feed_is_a_row_and_not_a_call_so_three_dead_calls_on_one_venue_are_one_feed()
    {
        var dead = TimeSpan.FromDays(3).TotalSeconds;
        var rows = new[] { Rows.At(Rows.Venue(1), price: dead, openInterest: dead, depth: dead), Rows.At(Rows.Venue(2)) };
        Assert.Equal(Statement.OneFeedDegraded, Statement.Verdict(rows));
    }

    [Fact]
    public void The_late_sentence_names_the_call_and_says_how_old_it_is()
    {
        var rows = new[] { Rows.At(Rows.Venue(1), depth: 81), Rows.At(Rows.Venue(2)) };
        Assert.Equal("The oldest is depth, 81 seconds behind.", Statement.Verdict(rows));
    }

    [Fact]
    public void Nothing_observed_and_nothing_graded_are_two_different_silences()
    {
        // No call has ever landed: nothing is late, because nothing has happened.
        var never = new[]
        {
            Rows.At(Rows.Venue(1), price: null, openInterest: null, depth: null),
            Rows.At(Rows.Venue(2), price: null, openInterest: null, depth: null)
        };
        Assert.Equal(Statement.NeverObserved, Statement.Verdict(never));

        // Observations with no stated cadence. We have the figures and no window to judge them
        // against, so the page declines to judge them rather than inventing one.
        var unjudged = new[] { Rows.At(Rows.Venue(1), price: 900, window: null) };
        Assert.Equal(Statement.NoCadence, Statement.Verdict(unjudged));
    }

    [Fact]
    public void The_sentence_moves_with_the_clock_and_not_with_the_render()
    {
        // The whole defect in one assertion: the same rows, two instants, two sentences. Nothing
        // about the data changed — only how long ago the call landed — and the server's own answer
        // is different, which is why the client has to be able to reach the same answer without a
        // round trip.
        var row = Rows.Venue(1);
        var fresh = new[] { Rows.At(row), Rows.At(Rows.Venue(2)) };
        var later = new[] { Rows.At(row, price: Rows.Window * 2), Rows.At(Rows.Venue(2)) };
        var muchLater = new[] { Rows.At(row, price: TimeSpan.FromDays(3).TotalSeconds), Rows.At(Rows.Venue(2)) };

        Assert.Equal(Statement.InsideTheWindow, Statement.Verdict(fresh));
        Assert.Equal("The oldest is price, 60 seconds behind.", Statement.Verdict(later));
        Assert.Equal(Statement.OneFeedDegraded, Statement.Verdict(muchLater));
    }

    // ── The sentence and the chips read one clock ────────────────────────────────────────────────
    // The first repair made the sentence move and left the chips where the render found them, which
    // did not add a defect so much as promote one: the largest type on the page announced that a
    // feed had stopped meaning anything while the acid-green BEST chip sat on that feed's own cells.
    // Both now come off the same predicate against the same three windows, and this is the assertion
    // that fails if they are ever allowed to answer differently.

    [Theory]
    [InlineData(0)]                                              // just landed
    [InlineData(Rows.Window * 3)]                                // past its window: △, and still ranked
    [InlineData(Rows.Window * Freshness.DegradedWindows)]        // exactly the boundary
    [InlineData(Rows.Window * 40)]
    public void The_sentence_and_the_chips_never_disagree_about_a_dead_feed(double priceAge)
    {
        // Row 1 holds the top bid, the deepest size and the largest turnover, so on a live page it
        // wears a chip in every ticker column that has one — and it is the row whose price call is
        // being aged. Whatever the sentence says about it, the chips have to be saying too.
        var page = new[]
        {
            Rows.At(Rows.Venue(1, bid: 100_000, bidSize: 999, turnover: 9_000_000), price: priceAge),
            Rows.At(Rows.Venue(2, bid: 99_900, bidSize: 10, turnover: 1_000_000)),
            Rows.At(Rows.Venue(3, bid: 99_800, bidSize: 8, turnover: 500_000))
        };

        var saysDead = Statement.Verdict(page)
            .Contains("stopped meaning anything", StringComparison.Ordinal);
        Assert.Equal(Freshness.Degraded(priceAge, Rows.Window), saysDead);

        var cells = RowCells
            .Build(page[0], Verdicts.Compute(page), ColumnScales.Compute(page))
            .Where(c => c.Tint == CallTone.Ticker)
            .ToList();

        if (saysDead)
        {
            // Nothing comparative anywhere on the row the sentence has just written off — not the
            // chip, not the spread's own word for it, and not the bar, which says the same thing
            // quietly.
            Assert.All(cells, c =>
            {
                Assert.Equal(Verdict.None, c.Verdict);
                Assert.Equal(SpreadBand.None, c.Band);
                Assert.Null(c.BarWidth);
            });
        }
        else
        {
            Assert.Contains(cells, c => c.Verdict == Verdict.Best);
            Assert.Contains(cells, c => c.BarWidth is not null);
        }
    }

    // ── The two renderers ───────────────────────────────────────────────────────────────────────
    // The sentence is said in two places: here, on the server, for the first paint and every live
    // push, and in studio-ages.js, which re-derives it every second from the same data-at / data-win
    // attributes it advances the ages from. That duplication is deliberate — the alternative was to
    // leave the sentence still while the ages under it moved, or to caveat it into meaninglessness —
    // and this is the guard on it. It is the same shape as the constants the fade curve already
    // ships to the client rather than re-typing.

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    [Theory]
    [InlineData(Statement.InsideTheWindow)]
    [InlineData(Statement.NeverObserved)]
    [InlineData(Statement.NoCadence)]
    [InlineData(Statement.OneFeedDegraded)]
    [InlineData(" feeds have stopped meaning anything.")]
    [InlineData("The oldest is ")]
    [InlineData(" seconds behind.")]
    public void The_client_says_the_same_words_the_server_does(string sentence) =>
        Assert.Contains(sentence, Read("studio-ages.js"), StringComparison.Ordinal);

    [Fact]
    public void The_client_is_wired_to_the_element_the_view_marks()
    {
        // Without the hook in the markup the script writes nothing and the sentence silently stops
        // moving again — which is a failure that looks exactly like a healthy page.
        Assert.Contains("data-statement-verdict", Read("_Statement.cshtml"), StringComparison.Ordinal);
        Assert.Contains("data-statement-verdict", Read("studio-ages.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_withdraws_a_chip_by_asking_the_question_the_sentence_asks()
    {
        // The sentence counts feeds that have stopped meaning anything; the withdrawal takes the
        // chips off those same feeds. One predicate, over the floor, exponent and degraded-window
        // count the sheet ships from Freshness — so the two halves of the page cannot come to
        // different conclusions about the same call, which is the whole failure being closed here.
        var js = Read("studio-ages.js");
        Assert.Contains("degraded(ageOf(", js, StringComparison.Ordinal);
        Assert.Contains("dataset.degradedWindows", js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_statement_is_a_live_region_so_a_push_redraws_it_too()
    {
        // Belt and braces: with a stream open the server re-renders this partial, and it can only
        // land if the region is named.
        Assert.Contains("data-live-region=\"statement\"", Read("_Statement.cshtml"), StringComparison.Ordinal);
    }

    // ── The counts ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_venue_is_an_order_book_and_a_listing_is_an_instrument()
    {
        // Binance listing the pair against two quotes in the same family: one platform, one order
        // book, two rows. The pair list card the reader clicked to get here says "1 venue ·
        // 2 listings", and the page it opens has to agree with it.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, quote: "USDT", segment: "binance-usdm")),
            Rows.At(Rows.Venue(2, quote: "USDC", segment: "binance-usdm")),
            Rows.At(Rows.Venue(3, quote: "USD", segment: "kraken-futures"))
        };

        Assert.Equal(2, Statement.Venues(rows));
        Assert.Equal(2, Statement.Platforms(rows));
        Assert.Equal(3, Statement.Listings(rows));
    }

    [Fact]
    public void The_view_prints_the_counted_numbers_and_not_the_row_count()
    {
        // The defect was not in the arithmetic — there was none. It was the view reaching for
        // Model.Rows.Count three times and labelling it venues, then platforms. Counting rows is
        // right for exactly one of the three words, and the view no longer decides which.
        var view = Read("_Statement.cshtml");
        Assert.Contains("Statement.Venues(Model.Rows)", view, StringComparison.Ordinal);
        Assert.Contains("Statement.Platforms(Model.Rows)", view, StringComparison.Ordinal);
        Assert.Contains("Statement.Listings(Model.Rows)", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Model.Rows.Count", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_segments_on_one_exchange_are_two_venues_on_one_platform()
    {
        // A spot book and a perp book are two books, which is why the pair list counts segments.
        // They are still one exchange, which is why the eyebrow counts those separately.
        var rows = new[]
        {
            Rows.At(Rows.Venue(1, segment: "kraken-futures")),
            Rows.At(Rows.Venue(2, segment: "kraken-spot"))
        };

        Assert.Equal(2, Statement.Venues(rows));
        Assert.Equal(1, Statement.Platforms(rows));
    }

    // ── The header's collected mark ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_collected_span_covers_all_three_calls_and_not_the_ticker_alone()
    {
        // The stamp used to be max(received_at): the price call of the healthiest row, standing in
        // for the whole page. A depth sweep or an open-interest call that has not landed in days
        // was invisible to it, on the one surface whose thesis is that those are separate clocks.
        var now = new DateTime(2026, 9, 6, 15, 33, 18, DateTimeKind.Utc);
        var row = Rows.Venue(1) with
        {
            ReceivedAt = now,
            OpenInterestAt = now.AddMinutes(-4),
            DepthAt = now.AddHours(-6)
        };

        Assert.Equal(3, PairPageModel.Observations([Rows.At(row)]).Count());

        var (from, to) = PairPageModel.CollectedSpan([Rows.At(row)]);
        Assert.Equal(now.AddHours(-6), from);
        Assert.Equal(now, to);
    }

    [Fact]
    public void The_span_reaches_back_to_the_frozen_venue_rather_than_reporting_the_healthy_one()
    {
        var now = new DateTime(2026, 9, 6, 15, 33, 18, DateTimeKind.Utc);
        var frozen = now.AddDays(-3);
        var rows = new[]
        {
            Rows.At(Rows.Venue(1) with { ReceivedAt = frozen, OpenInterestAt = frozen, DepthAt = frozen }),
            Rows.At(Rows.Venue(2) with { ReceivedAt = now, OpenInterestAt = now, DepthAt = now })
        };

        var (from, to) = PairPageModel.CollectedSpan(rows);
        Assert.Equal(frozen, from);
        Assert.Equal(now, to);
    }

    [Fact]
    public void A_page_where_nothing_has_ever_been_observed_has_no_collected_mark_at_all()
    {
        // A dash in the header, and never the render instant standing in for it: discovery lists an
        // instrument the moment the venue announces it and the first snapshot arrives later.
        var row = Rows.Venue(1) with { ReceivedAt = null, OpenInterestAt = null, DepthAt = null };
        Assert.Empty(PairPageModel.Observations([Rows.At(row)]));

        var (from, to) = PairPageModel.CollectedSpan([Rows.At(row)]);
        Assert.Null(from);
        Assert.Null(to);
    }

    // ── …and the header has to print the span it was handed ─────────────────────────────────────

    [Fact]
    public void A_span_days_wide_never_prints_as_one_clock()
    {
        // The verifier's seed, and the exact arithmetic that broke: one venue frozen at now − 3 days
        // puts both ends on the same second of the day. Compared as their formatted HH:mm:ssZ they
        // were EQUAL, the span collapsed, and the header printed the freshest end alone — over a
        // table one third of which had not been observed in three days.
        var to = new DateTime(2026, 9, 6, 17, 29, 26, DateTimeKind.Utc);
        var from = to.AddDays(-3);

        var stamp = CollectedStamp.Of(from, to);

        Assert.Contains("–", stamp.Text, StringComparison.Ordinal);
        Assert.Contains("2026-09-03", stamp.Text, StringComparison.Ordinal);
        Assert.Contains("2026-09-06", stamp.Text, StringComparison.Ordinal);
        Assert.Contains(Format.Utc(from), stamp.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_span_that_crosses_midnight_carries_its_dates()
    {
        // The second half of the same defect, and the one a reader cannot even suspect: three days
        // and three minutes printed as "15:30:11Z – 15:33:18Z" is a range every reader parses as
        // three minutes. Nothing in the string says otherwise.
        var from = new DateTime(2026, 9, 3, 15, 30, 11, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 6, 15, 33, 18, DateTimeKind.Utc);

        Assert.Equal("2026-09-03 15:30:11Z – 2026-09-06 15:33:18Z", CollectedStamp.Of(from, to).Text);
    }

    [Fact]
    public void Inside_one_day_it_is_still_two_clocks_and_no_dates()
    {
        // The date is left off only where leaving it off cannot mislead. Repeating today twice in a
        // header is noise, and the RENDERED stamp beside it already dates the document.
        var from = new DateTime(2026, 9, 6, 15, 30, 11, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 6, 15, 33, 18, DateTimeKind.Utc);

        Assert.Equal("15:30:11Z – 15:33:18Z", CollectedStamp.Of(from, to).Text);
    }

    [Fact]
    public void A_span_narrower_than_the_clock_is_the_one_instant_it_is()
    {
        // The behaviour the string comparison was reaching for, kept: the two ends print to the
        // second, so a span inside one second is a point rather than a range between two identical
        // strings. Decided on the INSTANTS truncated to that second, which is what stops three days
        // being decided the same way.
        var to = new DateTime(2026, 9, 6, 15, 33, 18, 900, DateTimeKind.Utc);
        var from = to.AddMilliseconds(-40);

        var stamp = CollectedStamp.Of(from, to);

        Assert.Equal("15:33:18Z", stamp.Text);
        Assert.DoesNotContain("–", stamp.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_observed_is_a_dash_and_never_the_render_instant()
    {
        Assert.Equal(Format.Dash, CollectedStamp.Of(null, null).Text);
    }

    [Fact]
    public void The_header_does_not_decide_the_shape_of_the_span_itself()
    {
        // Structural, and it is the fault rather than a style rule: the decision lived in the view
        // as `string.Equals(Format.UtcClock(from), Format.UtcClock(to))`, where nothing in CI could
        // read it, and it stayed wrong through a repair round written to fix exactly this.
        var view = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", "_Stamps.cshtml"));

        Assert.Contains("CollectedStamp.Of", view, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(Format.UtcClock", view, StringComparison.Ordinal);
    }
}

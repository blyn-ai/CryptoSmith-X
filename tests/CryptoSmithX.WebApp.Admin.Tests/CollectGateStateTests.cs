using CryptoSmithX.WebApp.Admin.Data;
using CryptoSmithX.WebApp.Admin.Models;

namespace CryptoSmithX.WebApp.Admin.Tests;

/// <summary>
/// The three-state collect column, and the one thing that can go wrong with a state that is derived
/// rather than stored: the two places that derive it drifting apart.
///
/// 0029 deliberately did not add a state column — NEW is
/// <c>collect = false and collect_changed_at is null</c>, read from the same columns the decision is
/// written to, so it cannot go stale. The cost of that choice is that the rule is spelled twice: once
/// in C# for everything the console renders (<see cref="CollectGate"/>), once in SQL for the filter
/// and the queue count (<see cref="InstrumentStore.UndecidedSql"/>), because a WHERE clause cannot
/// call into C#. These tests are what keeps the two spellings honest. There is no third copy, and a
/// third copy is the thing to reject in review.
///
/// No database is touched here — this project has never had a database-backed test — so the SQL side
/// is asserted as text. That is weaker than executing it, and the assertions below are written to be
/// worth something anyway: they pin the exact columns and the exact null test, which is what would
/// silently change.
/// </summary>
public sealed class CollectGateStateTests
{
    [Theory]
    // collect, changed at, what the console must say
    [InlineData(true, false, CollectState.On)]
    [InlineData(false, true, CollectState.Off)]
    [InlineData(false, false, CollectState.New)]
    public void The_three_situations_read_as_three_states(bool collect, bool stamped, CollectState expected) =>
        Assert.Equal(expected, CollectGate.State(collect, stamped ? DateTime.UtcNow : null));

    /// <summary>
    /// The fourth combination — collecting, with no stamp — is what the seeds and the auto-approve
    /// list produce, and what all 394 currently-collected rows on the test database look like. It is
    /// ON, not NEW: a listing that is being collected has had its answer, whoever gave it.
    /// </summary>
    [Fact]
    public void A_collected_row_with_no_stamp_is_on_not_new() =>
        Assert.Equal(CollectState.On, CollectGate.State(collect: true, collectChangedAt: null));

    /// <summary>
    /// The 1,864 instruments the owner switched off by hand carry both a timestamp and a note, which
    /// is exactly why they do not show up in the NEW queue. Measured on the test database before this
    /// screen was written: rows reading as NEW = 0, and every switched-off row is f/t/t.
    /// </summary>
    [Fact]
    public void A_hand_disabled_row_reads_as_off_not_new() =>
        Assert.Equal(CollectState.Off, CollectGate.State(false, new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void The_sql_predicate_tests_the_same_two_columns_as_the_csharp_one()
    {
        // Both halves, and nothing else. If someone relaxes this to `not collect`, the queue starts
        // showing the 1,864 rows an operator already ruled on and the screen becomes useless.
        Assert.Contains("collect = false", InstrumentStore.UndecidedSql);
        Assert.Contains("collect_changed_at is null", InstrumentStore.UndecidedSql);
        Assert.DoesNotContain("is not null", InstrumentStore.UndecidedSql);
    }

    /// <summary>
    /// Happened once: the doc comment above <see cref="InstrumentStore.UndecidedSql"/> spells the
    /// delisted exclusion as <c>&amp;lt;&amp;gt;</c> because a bare <c>&lt;&gt;</c> there would parse
    /// as an XML tag — correct escaping for a doc comment. The escaped form leaked into the actual
    /// string constant below it, where it is not HTML and Postgres has no idea what to do with an
    /// ampersand: every query built from this constant failed with `column "lt" does not exist` on
    /// every /Admin/Instruments load, filtered or not. The two string-content assertions above would
    /// not have caught it — neither touches this half of the predicate.
    /// </summary>
    [Fact]
    public void The_delisted_exclusion_is_real_sql_not_the_doc_comments_html_escaping()
    {
        Assert.Contains("status <> 'delisted'", InstrumentStore.UndecidedSql);
        Assert.DoesNotContain("&lt;", InstrumentStore.UndecidedSql);
        Assert.DoesNotContain("&gt;", InstrumentStore.UndecidedSql);
    }

    [Fact]
    public void The_new_filter_is_the_undecided_predicate_itself()
    {
        // Not a paraphrase of it — the same string, so the filter and the count cannot disagree.
        Assert.Equal(InstrumentStore.UndecidedSql, InstrumentStore.CollectWhere("new"));
    }

    [Fact]
    public void Switched_off_is_decided_off_not_merely_not_collecting()
    {
        // The distinction the whole change rests on: "off" must exclude the queue, or the two
        // filters would overlap and "off" would keep meaning what it meant before.
        var off = InstrumentStore.CollectWhere("off");
        Assert.NotNull(off);
        Assert.Contains("collect_changed_at is not null", off);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("everything")]
    // A hand-edited URL, and the shape an injection attempt would take.
    [InlineData("new'; drop table exchange_instrument; --")]
    public void An_unrecognised_filter_filters_nothing(string? filter) =>
        Assert.Null(InstrumentStore.CollectWhere(filter));

    /// <summary>
    /// The filter values the view offers must all be ones the store recognises. A typo in the view's
    /// option list would silently render an unfiltered page under a label promising a filter — the
    /// worst failure this screen has available, because the operator would read "new — waiting" over
    /// two thousand decided rows and conclude the queue was enormous.
    /// </summary>
    [Theory]
    [InlineData("new")]
    [InlineData("on")]
    [InlineData("off")]
    public void Every_offered_filter_is_recognised(string filter) =>
        Assert.NotNull(InstrumentStore.CollectWhere(filter));
}

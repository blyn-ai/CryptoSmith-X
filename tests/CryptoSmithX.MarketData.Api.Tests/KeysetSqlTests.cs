using CryptoSmithX.MarketData.Api;

namespace CryptoSmithX.MarketData.Api.Tests;

/// <summary>
/// The SQL fragment every paged endpoint appends. It is assembled from strings, so the compiler
/// cannot check it and CI has no Postgres to run it against — these assert the one property that
/// broke in production.
///
/// WHAT BROKE: three endpoints used a bare <c>''</c> as the tiebreak for tables whose primary key
/// already makes (instant, symbol) unique. That lands in ORDER BY as a bare string constant, and
/// Postgres rejects it outright — 42601, "non-integer constant in ORDER BY". Every one of those
/// three returned 500 on the first real request while the five endpoints with a genuine column
/// worked. Nothing short of a live database could have caught it, so the guard now lives in the
/// builder and is pinned here.
/// </summary>
public sealed class KeysetSqlTests
{
    [Fact]
    public void A_bare_string_constant_never_reaches_order_by()
    {
        // The exact input that produced 42601 in production.
        var sql = HistoryEndpoints.Keyset("received_at", "''");

        Assert.DoesNotContain("order by t.received_at, i.exchange_symbol, ''\n", sql);
        Assert.Contains("''::text", sql);
    }

    [Theory]
    [InlineData("''")]
    [InlineData(" '' ")]
    [InlineData("''::text")]
    public void Every_spelling_of_the_empty_tiebreak_ends_up_cast(string tiebreak)
    {
        var sql = HistoryEndpoints.Keyset("received_at", tiebreak);

        Assert.Contains("''::text", sql);
    }

    /// <summary>A real column is passed through untouched — the guard must not mangle the cases
    /// that were always correct.</summary>
    [Theory]
    [InlineData("t.venue_uid")]
    [InlineData("t.seq::text")]
    [InlineData("t.interval_s::text")]
    public void A_real_tiebreak_column_is_left_alone(string tiebreak)
    {
        var sql = HistoryEndpoints.Keyset("event_time", tiebreak);

        Assert.Contains(tiebreak, sql);
        Assert.DoesNotContain("''::text", sql);
    }

    /// <summary>
    /// The ordering and the resume predicate must name the same three expressions in the same order.
    /// If they ever diverge, a page resumes at a point the ordering never visits, and rows are
    /// skipped silently — the failure mode keyset paging exists to avoid.
    /// </summary>
    [Theory]
    [InlineData("received_at", "''::text")]
    [InlineData("event_time", "t.venue_uid")]
    [InlineData("bucket_time", "t.interval_s::text")]
    [InlineData("observed_at", "t.seq::text")]
    public void The_resume_predicate_matches_the_ordering(string timeColumn, string tiebreak)
    {
        var sql = HistoryEndpoints.Keyset(timeColumn, tiebreak);

        Assert.Contains($"(t.{timeColumn}, i.exchange_symbol, {tiebreak}) > (@cursorAt, @cursorSymbol, @cursorTie)", sql);
        Assert.Contains($"order by t.{timeColumn}, i.exchange_symbol, {tiebreak}", sql);
    }

    /// <summary>An absent cursor must not filter anything out: the first page starts at the window's
    /// own beginning, not at whatever a null compares greater than.</summary>
    [Fact]
    public void An_absent_cursor_disables_the_predicate_rather_than_matching_nothing()
    {
        var sql = HistoryEndpoints.Keyset("received_at", HistoryEndpoints.NoTiebreak);

        Assert.Contains("@cursorAt::timestamptz is null", sql);
        Assert.Contains(" or ", sql);
    }

    [Fact]
    public void Every_page_is_bounded()
    {
        var sql = HistoryEndpoints.Keyset("received_at", HistoryEndpoints.NoTiebreak);

        Assert.Contains("limit @limit", sql);
    }
}

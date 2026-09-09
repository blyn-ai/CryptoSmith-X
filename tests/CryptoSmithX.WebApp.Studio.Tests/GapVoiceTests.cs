using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Models;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// Band 5 printed the driver's own words on a public page.
///
/// The rows carried <c>NpgsqlException: Failed to connect to 172.22.0.2:5432</c> and
/// <c>40P01 deadlock detected</c>, and the band printed <c>cause — detail</c> verbatim: our internal
/// address, our internal port, in front of anyone who opened the asset. It was also not an answer to
/// the question the band exists for — a reader asks what they are missing, not which of our
/// containers could not reach which other one.
/// </summary>
public sealed class GapVoiceTests
{
    [Theory]
    [InlineData("NpgsqlException: Failed to connect to 172.22.0.2:5432")]
    [InlineData("40P01 deadlock detected")]
    [InlineData("Exception while reading from stream")]
    [InlineData("System.Net.Sockets.SocketException (111): Connection refused")]
    [InlineData("timeout connecting to redis://10.8.0.1:6379")]
    public void Nothing_the_driver_said_reaches_the_page(string detail)
    {
        Assert.Null(GapVoice.Detail(detail));
    }

    [Theory]
    [InlineData("HTTP 429, code=-1121, retry later", "venue code -1121")]
    [InlineData("code 10007 from the venue", "venue code 10007")]
    [InlineData("gave up on attempt 5", "attempt 5")]
    [InlineData("retry: 3 of 5", "attempt 3")]
    public void The_two_things_a_reader_can_act_on_do(string detail, string expected)
    {
        Assert.Equal(expected, GapVoice.Detail(detail));
    }

    [Fact]
    public void Every_cause_the_database_allows_has_a_sentence()
    {
        // The set is closed by a CHECK constraint since 0017. A cause added there and not here says
        // the neutral thing rather than leaking the raw token.
        string[] allowed =
        [
            "rate_limited", "timeout", "ws_sequence_gap", "ws_disconnected",
            "resync", "exchange_maintenance", "collector_down", "error",
        ];

        foreach (var cause in allowed)
        {
            var said = GapVoice.Say(cause);
            Assert.NotEqual("Collection stopped", said);
            Assert.DoesNotContain('_', said);
        }

        Assert.Equal("Collection stopped", GapVoice.Say("something_new_in_0099"));
    }

    private static GapRow Row(string collector, string cause, int minute, bool closed = true, string? detail = null) =>
        new("binance-usdm", collector, new DateTime(2026, 9, 9, 4, minute, 0, DateTimeKind.Utc),
            closed ? new DateTime(2026, 9, 9, 6, minute, 0, DateTimeKind.Utc) : null, cause, detail);

    [Fact]
    public void Nine_breaks_of_one_kind_are_one_line_that_says_nine()
    {
        var lines = GapLine.Fold(Enumerable.Range(0, 9).Select(i => Row("candles", "error", i)));

        var line = Assert.Single(lines);
        Assert.Equal(9, line.Count);
        Assert.Equal(0, line.From.Minute);
        Assert.Equal(8, line.To!.Value.Minute);
    }

    [Fact]
    public void An_open_break_is_never_folded_into_a_closed_one_and_comes_first()
    {
        var lines = GapLine.Fold([
            Row("candles", "error", 1),
            Row("candles", "error", 2, closed: false),
        ]);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Open);
        Assert.Null(lines[0].To);
    }

    [Fact]
    public void A_detail_two_breaks_disagree_about_is_carried_by_neither()
    {
        // Two venue codes folded onto one line would attribute one break's cause to the other's.
        var lines = GapLine.Fold([
            Row("candles", "error", 1, detail: "code=-1121"),
            Row("candles", "error", 2, detail: "code=-1003"),
        ]);

        Assert.Null(Assert.Single(lines).Detail);
    }
}

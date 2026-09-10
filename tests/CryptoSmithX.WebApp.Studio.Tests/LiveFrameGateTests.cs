using CryptoSmithX.WebApp.Studio.Live;
using Microsoft.AspNetCore.Http;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The live streams' own ceiling, and the per-address one the Latest gate has never needed.
///
/// A Latest stream wakes on a collector pass and renders from a cache every watcher shares; a live
/// stream is a viewer in a room computing five times a second, held open the whole time. So the
/// number is lower — and it is also per address, because one script opening tabs could take a
/// meaningful share of the room budget on its own.
/// </summary>
public sealed class LiveFrameGateTests
{
    [Fact]
    public void The_process_ceiling_refuses_the_stream_past_it()
    {
        var gate = new LiveFrameGate(max: 2, perAddress: 10);

        Assert.True(gate.TryEnter("a"));
        Assert.True(gate.TryEnter("b"));
        Assert.False(gate.TryEnter("c"));
    }

    [Fact]
    public void One_address_cannot_take_more_than_its_share()
    {
        var gate = new LiveFrameGate(max: 50, perAddress: 3);

        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.False(gate.TryEnter("1.2.3.4"));

        // Somebody else is unaffected — the refusal is about one address, not about the site.
        Assert.True(gate.TryEnter("5.6.7.8"));
    }

    [Fact]
    public void A_slot_given_back_can_be_taken_again()
    {
        var gate = new LiveFrameGate(max: 1, perAddress: 1);

        Assert.True(gate.TryEnter("a"));
        gate.Exit("a");

        Assert.Equal(0, gate.Open);
        Assert.True(gate.TryEnter("a"));
    }

    [Fact]
    public void An_address_that_left_leaves_no_trace_behind_it()
    {
        // This dictionary is keyed by something a caller chooses. An entry that is never removed is
        // a slow leak an anonymous surface can be walked into, one address at a time.
        var gate = new LiveFrameGate(max: 50, perAddress: 3);
        for (var i = 0; i < 100; i++)
        {
            var address = $"10.0.0.{i}";
            Assert.True(gate.TryEnter(address));
            gate.Exit(address);
        }

        Assert.Equal(0, gate.Open);
    }

    [Fact]
    public void The_shipped_ceilings_are_the_ones_the_plan_proposed() =>
        Assert.Equal((50, 3), (LiveFrameGate.MaxStreams, LiveFrameGate.MaxPerAddress));

    [Theory]
    [InlineData("9.9.9.9", "1.1.1.1, 2.2.2.2", "9.9.9.9")]   // Cloudflare's own header wins
    [InlineData(null, "1.1.1.1, 2.2.2.2", "1.1.1.1")]        // else the first of the forwarded list
    [InlineData(null, null, "203.0.113.7")]                   // else the socket itself
    public void The_address_is_read_in_the_order_that_cannot_be_spoofed(string? cloudflare, string? forwarded, string expected)
    {
        // CF-Connecting-IP first because it is the one this site actually sits behind and the only
        // one a client cannot set. X-Forwarded-For arrives as a list a caller can prepend to, so its
        // first entry is the client only when everything in front of us is trusted.
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        if (cloudflare is not null)
        {
            context.Request.Headers["CF-Connecting-IP"] = cloudflare;
        }

        if (forwarded is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwarded;
        }

        Assert.Equal(expected, LiveFrameGate.AddressOf(context));
    }
}

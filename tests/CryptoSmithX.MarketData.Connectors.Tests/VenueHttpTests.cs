using System.Net;
using System.Reflection;
using CryptoSmithX.MarketData.Connectors.Http;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// Pins the two settings plans/prompt-rest-hygiene.md measured and asked for — a bare
/// <c>new HttpClient()</c> carries neither, and that absence is exactly what was costing 42% of the
/// bandwidth budget and the occasional cold TLS handshake. Read via reflection into the private
/// <see cref="SocketsHttpHandler"/> HttpClient wraps: there is no public surface on HttpClient itself
/// that exposes decompression or pool-idle settings, so this is the only way to assert them without
/// making an actual request.
/// </summary>
public sealed class VenueHttpTests
{
    private static SocketsHttpHandler Handler()
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("HttpMessageInvoker._handler not found — .NET internals moved");
        var handler = field.GetValue(VenueHttp.Shared);

        // HttpClient wraps the handler in a DiagnosticsHandler by default; unwrap to the real one.
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        return Assert.IsType<SocketsHttpHandler>(handler);
    }

    [Fact]
    public void Compression_is_requested_over_all_three_encodings_the_venues_might_answer_with()
    {
        var expected = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli;
        Assert.Equal(expected, Handler().AutomaticDecompression);
    }

    /// <summary>Above every pass cadence in this codebase with room to spare — the point is not to
    /// match one collector's interval exactly, it is to outlive all of them so a warm connection is
    /// still warm the next time any caller on this shared client needs it.</summary>
    [Fact]
    public void Pooled_connections_outlive_a_one_minute_pass_cadence()
    {
        Assert.True(Handler().PooledConnectionIdleTimeout > TimeSpan.FromMinutes(1));
    }

    /// <summary>One instance, not four — see VenueHttp's own doc comment for why a shared client does
    /// not mean a shared connection pool across venues.</summary>
    [Fact]
    public void The_same_instance_is_exposed_every_time()
    {
        Assert.Same(VenueHttp.Shared, VenueHttp.Shared);
    }
}

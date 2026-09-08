using System.Net;

namespace CryptoSmithX.MarketData.Connectors.Pacing;

/// <summary>
/// A venue saying "not so fast", carrying the venue's own answer to "how long". Plain
/// <see cref="HttpRequestException"/> keeps the status code but not the headers, so a
/// <c>Retry-After</c> the venue took the trouble to send used to be dropped between the client that
/// saw it and the gate that needed it — <see cref="VenueGate.Penalize(TimeSpan)"/> has existed since
/// 0021 and nothing ever called it with a real number.
///
/// Derives from <see cref="HttpRequestException"/> with the 429 status still set, so every existing
/// <c>is HttpRequestException { StatusCode: TooManyRequests }</c> test keeps matching.
/// </summary>
public sealed class VenueRateLimitedException : HttpRequestException
{
    public VenueRateLimitedException(string message, TimeSpan? retryAfter)
        : base(message, null, HttpStatusCode.TooManyRequests) => RetryAfter = retryAfter;

    /// <summary>What the venue asked for, when it said. Null when it only refused.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Turning one caller's refusal into the whole IP's pacing.</summary>
public static class VenuePenalty
{
    /// <summary>
    /// Parks the venue if <paramref name="ex"/> is a refusal, preferring the venue's own
    /// <c>Retry-After</c> over our conservative default. Anything else is left alone: a broken
    /// symbol is not a reason to slow every other caller down.
    /// </summary>
    public static void Apply(VenueGate gate, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(gate);

        switch (ex)
        {
            case VenueRateLimitedException { RetryAfter: { } after } when after > TimeSpan.Zero:
                gate.Penalize(after);
                break;
            case HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }:
                gate.Penalize();
                break;
        }
    }
}

/// <summary>Reading a venue's refusal off the wire, in the one place every client already had.</summary>
public static class VenueResponseExtensions
{
    /// <summary>
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> plus the one distinction the gate
    /// cares about: a 429 becomes a <see cref="VenueRateLimitedException"/> carrying the venue's
    /// <c>Retry-After</c>, in either form the header allows — a delay or an absolute date.
    /// </summary>
    public static HttpResponseMessage EnsureVenueSuccess(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new VenueRateLimitedException(
                $"{response.RequestMessage?.RequestUri} answered 429", RetryAfterOf(response));
        }

        return response.EnsureSuccessStatusCode();
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        // The absolute form is an instant on the VENUE's clock, so it is subtracted from the venue's
        // own Date header rather than from ours: two clocks that disagree by a minute would otherwise
        // turn a 10 s cooldown into a minute of silence, or into nothing at all. No Date header, no
        // usable answer — the caller falls back to the default penalty, which is the honest outcome.
        return header.Date is { } date && response.Headers.Date is { } served ? date - served : null;
    }
}

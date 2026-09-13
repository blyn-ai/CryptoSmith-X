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
    /// cares about: a refusal for going too fast becomes a <see cref="VenueRateLimitedException"/>
    /// carrying the venue's <c>Retry-After</c>, in either form the header allows — a delay or an
    /// absolute date.
    ///
    /// <b>418 is one of those refusals, and learning that cost a ban.</b> Binance answers 429 while
    /// it is warning you and 418 — "I'm a teapot" — once it has actually banned the address, and the
    /// second one is the one that matters. Read as an ordinary failure it is invisible to the
    /// pacing: the gate keeps the pace that earned the ban, every collector on that host fails for
    /// as long as it lasts, and the bans escalate from minutes to days. Seen on api.binance.com the
    /// first hour spot was switched on.
    ///
    /// A joke status code is a poor place to put a ban, and no other venue here uses it — which is
    /// exactly why it has to be named rather than left to a general "not a success".
    /// </summary>
    public static HttpResponseMessage EnsureVenueSuccess(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode is HttpStatusCode.TooManyRequests or Banned)
        {
            throw new VenueRateLimitedException(
                $"{response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}",
                RetryAfterOf(response) ?? DefaultBanCooldown(response.StatusCode));
        }

        return response.EnsureSuccessStatusCode();
    }

    /// <summary>Binance's "you are banned, not merely warned". <see cref="HttpStatusCode"/> has no
    /// name for it.</summary>
    private const HttpStatusCode Banned = (HttpStatusCode)418;

    /// <summary>
    /// How long to stand down when the venue banned us and did not say for how long.
    ///
    /// Binance's shortest ban is two minutes and they escalate on repetition, so guessing short is
    /// the expensive direction to be wrong in. A plain 429 keeps the gate's own default — that is a
    /// warning, and the gate already knows what to do with one.
    /// </summary>
    private static TimeSpan? DefaultBanCooldown(HttpStatusCode status) =>
        status == Banned ? TimeSpan.FromMinutes(5) : null;

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

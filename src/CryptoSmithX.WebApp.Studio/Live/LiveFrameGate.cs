namespace CryptoSmithX.WebApp.Studio.Live;

/// <summary>
/// How many LIVE streams this process will hold at once, and how many from one address.
///
/// Separate from <see cref="LiveStreamGate"/> and lower, because the two cost different things. A
/// Latest stream wakes on a collector pass — seconds apart — and renders from a cache every watcher
/// shares. A live stream is a viewer in a room that computes five times a second and a connection
/// held open the whole time. The hundred that reads as cautious for the first is not the same
/// hundred for the second.
///
/// <b>And a per-address ceiling, which the Latest gate has never needed.</b> That one is refused only
/// when the whole process is full; here one script opening tabs could take a meaningful share of the
/// room budget on its own. Three is what a person plausibly has open on one machine.
///
/// Both numbers are starting proposals in the plan's own words, to be settled by measurement rather
/// than argument — but they are constants here and not configuration, for <see cref="LiveStreamGate"/>'s
/// reason: a limit that can be raised from a config file is a limit that gets raised instead of read.
/// </summary>
public sealed class LiveFrameGate
{
    public const int MaxStreams = 50;

    public const int MaxPerAddress = 3;

    private readonly int _max;
    private readonly int _perAddress;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _byAddress = new(StringComparer.Ordinal);
    private int _open;

    public LiveFrameGate()
        : this(MaxStreams, MaxPerAddress)
    {
    }

    /// <summary>Injectable for the tests only — see the class remarks.</summary>
    internal LiveFrameGate(int max, int perAddress)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perAddress, 1);
        _max = max;
        _perAddress = perAddress;
    }

    public int Open
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <summary>Takes a slot for this address, or refuses. Under one lock rather than two counters:
    /// checking the total and the per-address count separately lets a burst pass both checks and
    /// then exceed one of them, which is a limit that is briefly not one.</summary>
    public bool TryEnter(string address)
    {
        lock (_gate)
        {
            if (_open >= _max)
            {
                return false;
            }

            _byAddress.TryGetValue(address, out var mine);
            if (mine >= _perAddress)
            {
                return false;
            }

            _open++;
            _byAddress[address] = mine + 1;
            return true;
        }
    }

    /// <summary>Gives a slot back. The address's own entry is removed at zero rather than left at
    /// zero: this dictionary is keyed by something a caller chooses, so a key that is never removed
    /// is a slow leak an anonymous surface can be walked into.</summary>
    public void Exit(string address)
    {
        lock (_gate)
        {
            if (_open > 0)
            {
                _open--;
            }

            if (!_byAddress.TryGetValue(address, out var mine))
            {
                return;
            }

            if (mine <= 1)
            {
                _byAddress.Remove(address);
            }
            else
            {
                _byAddress[address] = mine - 1;
            }
        }
    }

    /// <summary>
    /// Who is asking, for the per-address ceiling.
    ///
    /// Cloudflare's own header first, because it is the one this site actually sits behind and the
    /// only one here that a client cannot set: <c>X-Forwarded-For</c> arrives as a list a caller can
    /// prepend to, so its FIRST entry is the client only when everything in front is trusted. Read
    /// in this order the ceiling is right behind the CDN and merely best-effort without it, which is
    /// the honest reading — and a ceiling that can be walked around by setting a header would be a
    /// ceiling that exists only against people who are not trying.
    /// </summary>
    public static string AddressOf(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("CF-Connecting-IP", out var cloudflare)
            && !string.IsNullOrWhiteSpace(cloudflare))
        {
            return cloudflare.ToString();
        }

        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
            && !string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.ToString().Split(',')[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

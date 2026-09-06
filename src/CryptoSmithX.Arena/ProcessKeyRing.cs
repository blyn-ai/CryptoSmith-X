using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace CryptoSmithX.Arena;

/// <summary>
/// A key ring that lives in this process and nowhere else.
///
/// Data Protection needs somewhere to keep its keys, and with nothing configured it finds
/// <c>~/.aspnet/DataProtection-Keys</c> — inside the container, on a layer that is thrown away on
/// every deployment — writes a key there, and warns that the key is unencrypted at rest. Arena
/// protects nothing: no sign-in, no cookie, no antiforgery token, every page identical for every
/// visitor. So the honest configuration is a key ring that admits it is per-process, rather than a
/// file whose only readers are the two minutes before the next deploy.
///
/// <b>Why this class exists at all, and what was rejected.</b> The obvious line —
/// <c>AddDataProtection().UseEphemeralDataProtectionProvider()</c> — does not do this, and the
/// comment that used to sit above it in <c>Program.cs</c> was a false statement about the running
/// system. That call replaces the <c>IDataProtectionProvider</c> registration only; the default
/// file-backed key ring and the hosted service that eagerly initialises it at startup stay
/// registered, so the key file is still written and the warning still logged. Measured, not assumed:
/// with that line the probe wrote <c>key-b1e073a6-….xml</c> under HOME and logged "No XML encryptor
/// configured"; with the line DELETED it wrote a key too, because the hosting layer registers Data
/// Protection whether or not anything asks for it. Replacing the repository is the smallest change
/// that actually stops the write, and it uses public API — the alternative, unregistering the
/// framework's internal <c>DataProtectionHostedService</c> by type name, silences the startup
/// warning as well but breaks silently the day that type is renamed, which is the worst way for a
/// key ring to change behaviour.
///
/// One thing this does NOT remove: <c>XmlKeyManager</c> still logs its "key may be persisted to
/// storage in unencrypted form" warning when it mints the per-process key, because that warning is
/// about the absence of an encryptor and not about the repository. In this configuration it is a
/// warning about something that cannot happen — nothing here reaches storage. It is left visible
/// rather than filtered away, because a log filter on that category would also hide a real Data
/// Protection failure, and hiding a warning is not the same as making it untrue.
///
/// If a later step gives Arena a form, this class is what to revisit: an antiforgery token protected
/// by a per-process key stops validating across a restart, and across a second replica.
/// </summary>
public sealed class ProcessKeyRing : IXmlRepository
{
    // Written once at startup and read once; the lock is here because IXmlRepository makes no
    // single-threaded promise and a key ring re-created on a background refresh would otherwise race
    // the read. Contention is not a consideration — this list holds one element for the life of the
    // process.
    private readonly Lock _gate = new();
    private readonly List<XElement> _elements = [];

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate)
        {
            return _elements.ToArray();
        }
    }

    public void StoreElement(XElement element, string? friendlyName)
    {
        lock (_gate)
        {
            _elements.Add(element);
        }
    }
}

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Node.Interfaces;

/// <summary>
/// Our Tor v3 onion service (<c>Node:Tor:OnionServiceEnabled</c>), kept registered with Tor's control port for the
/// node's lifetime.
/// </summary>
public interface ITorOnionService : IAnnouncedAddressSource
{
    /// <summary>The onion host name (<c>&lt;56 chars&gt;.onion</c>) once Tor accepted the service; null before.</summary>
    string? OnionHost { get; }

    /// <summary>The virtual port peers dial.</summary>
    ushort OnionPort { get; }

    /// <summary>
    /// Starts registering the service in the background (retried while Tor cannot be reached); does nothing when the
    /// onion service is off. Never fails the node's start.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Closes the control connection, which removes the service from Tor.</summary>
    Task StopAsync();
}
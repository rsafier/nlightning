namespace NLightning.Domain.Node.Interfaces;

using Gossip.Addresses;

/// <summary>
/// Addresses of ours that are known only at run time (our Tor onion service), announced in our
/// <c>node_announcement</c> next to <c>Gossip:AnnounceAddresses</c>.
/// </summary>
public interface IAnnouncedAddressSource
{
    /// <summary>The addresses to announce now; empty while there are none.</summary>
    IReadOnlyList<AddressDescriptor> GetAnnouncedAddresses();

    /// <summary>Raised when <see cref="GetAnnouncedAddresses"/> changed.</summary>
    event EventHandler? AnnouncedAddressesChanged;
}
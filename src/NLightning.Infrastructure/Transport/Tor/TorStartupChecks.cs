using System.Net;

namespace NLightning.Infrastructure.Transport.Tor;

using Domain.Gossip.Addresses;
using Domain.Node.Options;

/// <summary>
/// Settings that work but undo part of what <c>Node:Tor</c> is for; the host logs them at start.
/// </summary>
public static class TorStartupChecks
{
    /// <summary>
    /// The warnings for these settings (empty when there are none).
    /// </summary>
    /// <param name="nodeOptions">The node options (<c>Node:Tor</c>, <c>Node:ListenAddresses</c>).</param>
    /// <param name="announcedAddresses">Our configured <c>Gossip:AnnounceAddresses</c>.</param>
    public static IReadOnlyList<string> GetWarnings(NodeOptions nodeOptions,
                                                    IEnumerable<AddressDescriptor> announcedAddresses)
    {
        var tor = nodeOptions.Tor;
        var warnings = new List<string>();
        if (!tor.IsTorOnly)
            return warnings;

        // Refused at validation unless Tor:AllowClearnetListen is set (NL-577); then a reminder
        foreach (var listen in nodeOptions.ListenAddresses)
            if (IPEndPoint.TryParse(listen, out var endPoint) && !IPAddress.IsLoopback(endPoint.Address))
                warnings.Add($"Tor-only mode listens on {listen} (Tor:AllowClearnetListen), reachable without Tor; "
                           + "listen on 127.0.0.1 (the onion service's target) to be reachable through Tor only");

        foreach (var descriptor in announcedAddresses)
            if (descriptor.Type is AddressDescriptorType.IPv4 or AddressDescriptorType.IPv6
                                   or AddressDescriptorType.Dns)
                warnings.Add($"Tor-only mode announces the clearnet address {descriptor.Host}:{descriptor.Port} "
                           + "(Gossip:AnnounceAddresses); peers can link it to this node");

        if (!tor.IsOnionServiceEnabled)
            warnings.Add("Tor-only mode without an onion service (Node:Tor:OnionServiceEnabled false): peers cannot "
                       + "connect to us unless an onion address is announced by hand (Gossip:AnnounceAddresses)");

        return warnings;
    }
}
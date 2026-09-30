using System.Net;
using System.Net.Sockets;

namespace NLightning.Domain.Node.Bootstrap;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// A peer a BOLT 10 DNS seed pointed to: the node id decoded from the SRV target's first label, one of the target's
/// addresses and the SRV port. Nothing here is authenticated; the BOLT 8 handshake proves the node id.
/// </summary>
/// <param name="NodeId">The node id of the virtual host label.</param>
/// <param name="Address">An address of the virtual host.</param>
/// <param name="Port">The SRV record's port.</param>
/// <param name="Seed">The seed root the record came from.</param>
public readonly record struct SeedPeerCandidate(CompactPubKey NodeId, IPAddress Address, ushort Port, string Seed)
{
    /// <summary>
    /// The Tor v3 onion host (<c>&lt;56 chars&gt;.onion</c>) of a graph candidate reached through Tor; null for an IP
    /// candidate, whose <see cref="Address"/> is then the one dialed (it is <see cref="IPAddress.None"/> for an onion).
    /// </summary>
    public string? OnionHost { get; init; }

    /// <summary>
    /// The dialed endpoint as text: the IP address (IPv6 without brackets) or the onion host, and the port. Dial
    /// failures are remembered by it.
    /// </summary>
    public (string Host, ushort Port) Endpoint => (OnionHost ?? Address.ToString(), Port);

    /// <summary>
    /// The address as <c>pubkey@host:port</c>, with an IPv6 host in brackets (<c>pubkey@[2001:db8::1]:9735</c>).
    /// </summary>
    public PeerAddressInfo ToPeerAddressInfo()
    {
        var host = OnionHost
                ?? (Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString());
        return new PeerAddressInfo($"{NodeId}@{host}:{Port}");
    }
}
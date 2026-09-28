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
    /// The address as <c>pubkey@host:port</c>, with an IPv6 host in brackets (<c>pubkey@[2001:db8::1]:9735</c>).
    /// </summary>
    public PeerAddressInfo ToPeerAddressInfo()
    {
        var host = Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString();
        return new PeerAddressInfo($"{NodeId}@{host}:{Port}");
    }
}
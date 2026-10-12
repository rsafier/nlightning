using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Gossip.Relay;

using Domain.Node.Interfaces;
using Interfaces;

/// <summary>
/// The default <see cref="IGossipPeerDirectory"/>: the peers <see cref="IPeerManager"/> lists (it installs a connection
/// only after the <c>init</c> exchange). The peer manager is resolved on first use, because it depends (through the
/// channel update service) on the relay that uses this directory.
/// </summary>
public sealed class PeerManagerGossipPeerDirectory : IGossipPeerDirectory
{
    private readonly IServiceProvider _serviceProvider;
    private IPeerManager? _peerManager;

    public PeerManagerGossipPeerDirectory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public IReadOnlyList<GossipPeer> GetConnectedPeers()
    {
        _peerManager ??= _serviceProvider.GetService<IPeerManager>();
        if (_peerManager is null)
            return [];

        var peers = new List<GossipPeer>();
        foreach (var peer in _peerManager.ListPeers())
            if (peer.TryGetPeerService(out var service))
                peers.Add(new GossipPeer(peer.NodeId, service));

        return peers;
    }
}
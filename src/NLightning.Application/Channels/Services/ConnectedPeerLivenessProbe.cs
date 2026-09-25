using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Interfaces;

/// <summary>
/// The default <see cref="IPeerLivenessProbe"/>: a channel's link is up while <see cref="IPeerManager"/> still has the
/// very connection (the <c>PeerModel</c> instance, one per connection) that was current when the channel was marked
/// up. A reconnection gives the peer a new model, so the link stays down until the channel is marked up again (N7
/// channel_reestablish). Without a peer manager (in-process tests) every marked channel is up.
/// </summary>
/// <remarks>
/// The peer manager is resolved on first use, not injected: it depends on the channel manager, which the operations
/// and the scheduler sit next to.
/// </remarks>
public sealed class ConnectedPeerLivenessProbe : IPeerLivenessProbe
{
    /// <summary>The pinned "connection" of a channel marked up when there is no peer manager.</summary>
    private static readonly object s_noPeerManager = new();

    private readonly ConcurrentDictionary<ChannelId, object> _links = new();
    private readonly IServiceProvider _serviceProvider;
    private IPeerManager? _peerManager;
    private bool _resolved;

    public ConnectedPeerLivenessProbe(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                   CancellationToken cancellationToken = default)
    {
        if (!_links.TryGetValue(channelId, out var pinned))
            return Task.FromResult(false);

        var current = CurrentConnection(peerPubKey);
        return Task.FromResult(current is not null && ReferenceEquals(current, pinned));
    }

    /// <inheritdoc />
    public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey)
    {
        var current = CurrentConnection(peerPubKey);
        if (current is null)
            _links.TryRemove(channelId, out _);
        else
            _links[channelId] = current;
    }

    private object? CurrentConnection(CompactPubKey peerPubKey)
    {
        if (!_resolved)
        {
            _peerManager = _serviceProvider.GetService<IPeerManager>();
            _resolved = true;
        }

        return _peerManager is null ? s_noPeerManager : _peerManager.GetPeer(peerPubKey);
    }
}
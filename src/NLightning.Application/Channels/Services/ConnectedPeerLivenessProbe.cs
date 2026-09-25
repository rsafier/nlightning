using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Services;

using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Interfaces;

/// <summary>
/// The default <see cref="IPeerLivenessProbe"/>: a peer is alive when <see cref="IPeerManager"/> has a connection to
/// it. Without a peer manager (in-process tests) every peer is alive.
/// </summary>
/// <remarks>
/// The peer manager is resolved on first use, not injected: it depends on the channel manager, which the operations
/// and the scheduler sit next to. A real ping (BOLT 1) before committing to a quiet peer is a follow-up that needs the
/// last-message timestamp of the peer service.
/// </remarks>
public sealed class ConnectedPeerLivenessProbe : IPeerLivenessProbe
{
    private readonly IServiceProvider _serviceProvider;
    private IPeerManager? _peerManager;
    private bool _resolved;

    public ConnectedPeerLivenessProbe(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public Task<bool> IsAliveAsync(CompactPubKey peerPubKey, CancellationToken cancellationToken = default)
    {
        if (!_resolved)
        {
            _peerManager = _serviceProvider.GetService<IPeerManager>();
            _resolved = true;
        }

        return Task.FromResult(_peerManager is null || _peerManager.GetPeer(peerPubKey) is not null);
    }
}
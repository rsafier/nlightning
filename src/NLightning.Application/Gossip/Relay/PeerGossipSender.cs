using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Gossip.Relay;

using Domain.Gossip.Interfaces;
using Domain.Protocol.Interfaces;
using Interfaces;

/// <summary>
/// The default <see cref="IGossipPeerSender"/> (NL-351): queues on the peer's outbox through
/// <see cref="IPeerGossipOutbox"/> when one is registered (resolved on first use: the peer manager that implements it
/// depends on the relay through the channel update service), else awaits <c>IPeerService.SendGossipMessageAsync</c>.
/// </summary>
public sealed class PeerGossipSender : IGossipPeerSender
{
    private readonly IServiceProvider? _serviceProvider;
    private IPeerGossipOutbox? _outbox;
    private bool _resolved;

    public PeerGossipSender(IServiceProvider? serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>True when sends go through the peer's outbox (resolves the outbox on first use).</summary>
    public bool UsesOutbox => GetOutbox() is not null;

    /// <inheritdoc />
    public async ValueTask<bool> SendAsync(GossipPeer peer, IMessage message)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(message);

        if (GetOutbox() is { } outbox)
            return outbox.TryEnqueueGossip(peer.Service, message);

        await peer.Service.SendGossipMessageAsync(message);
        return true;
    }

    private IPeerGossipOutbox? GetOutbox()
    {
        if (_resolved)
            return _outbox;

        _outbox = _serviceProvider?.GetService<IPeerGossipOutbox>();
        _resolved = true;
        return _outbox;
    }
}
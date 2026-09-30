using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Safety;

using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;

/// <summary>
/// <see cref="IChannelErrorSender"/> over the peer's current connection (<see cref="IPeerManager"/>), through the
/// peer's <c>PeerOutbox</c> (<see cref="IPeerManager.TryEnqueueChannelError"/>): the error is sent without
/// disconnecting (BOLT 1 MAY) and keeps the outbox order, so it cannot overtake messages queued before it (NL-273).
/// </summary>
/// <remarks>The peer manager is resolved lazily (it depends on the channel manager).</remarks>
public sealed class PeerChannelErrorSender : IChannelErrorSender
{
    private readonly ILogger<PeerChannelErrorSender> _logger;
    private readonly IServiceProvider _serviceProvider;

    public PeerChannelErrorSender(ILogger<PeerChannelErrorSender> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public Task<bool> TrySendAsync(CompactPubKey peer, ErrorMessage error)
    {
        try
        {
            var peerManager = _serviceProvider.GetService<IPeerManager>();
            return Task.FromResult(peerManager is not null && peerManager.TryEnqueueChannelError(peer, error));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not send the channel error to peer {Peer}", peer);
            return Task.FromResult(false);
        }
    }
}
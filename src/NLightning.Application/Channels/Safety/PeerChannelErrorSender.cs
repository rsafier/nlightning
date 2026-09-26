using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Safety;

using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;

/// <summary>
/// <see cref="IChannelErrorSender"/> over the peer's current connection (<see cref="IPeerManager.GetPeer"/>), through
/// <c>IPeerService.SendErrorAsync</c>: the error is sent without disconnecting (BOLT 1 MAY).
/// </summary>
/// <remarks>
/// It bypasses the peer's <c>PeerOutbox</c>, so it can overtake messages queued there; that is harmless for a failed
/// channel (nothing is signed or accepted for it afterwards, and its stored error is re-sent on every connection). An
/// outbox-ordered send needs a <c>PeerManager</c> API (seam for the integrator). The peer manager is resolved lazily
/// (it depends on the channel manager).
/// </remarks>
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
    public async Task<bool> TrySendAsync(CompactPubKey peer, ErrorMessage error)
    {
        try
        {
            var peerManager = _serviceProvider.GetService<IPeerManager>();
            if (peerManager?.GetPeer(peer) is not { } peerModel || !peerModel.TryGetPeerService(out var peerService))
                return false;

            await peerService.SendErrorAsync(error);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not send the channel error to peer {Peer}", peer);
            return false;
        }
    }
}
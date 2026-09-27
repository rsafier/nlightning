using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Quiescence;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Interfaces;

/// <summary>
/// The production <see cref="IQuiescencePeerDisconnector"/>: a channel <c>warning</c>, then the peer's current
/// connection is closed through its <c>IPeerService</c> (not <see cref="IPeerManager.DisconnectPeer"/>, which would also
/// stop our reconnect loop).
/// </summary>
public sealed class PeerServiceQuiescenceDisconnector : IQuiescencePeerDisconnector
{
    private readonly ILogger<PeerServiceQuiescenceDisconnector> _logger;
    private readonly IServiceProvider _serviceProvider;

    public PeerServiceQuiescenceDisconnector(ILogger<PeerServiceQuiescenceDisconnector> logger,
                                             IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public void Disconnect(CompactPubKey peerPubKey, ChannelId channelId, string reason)
    {
        try
        {
            // Resolved lazily: the peer manager depends (through the channel manager) on the quiescence service
            var peer = _serviceProvider.GetService<IPeerManager>()?.GetPeer(peerPubKey);
            if (peer is null || !peer.TryGetPeerService(out var peerService))
            {
                _logger.LogInformation("Quiescence of channel {ChannelId} timed out; peer {Peer} is not connected",
                                       channelId, peerPubKey);
                return;
            }

            peerService.Disconnect(new ChannelWarningException(reason, channelId, reason) { CloseConnection = true });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to close the connection to {Peer} after the quiescence timeout of {ChannelId}",
                             peerPubKey, channelId);
        }
    }
}
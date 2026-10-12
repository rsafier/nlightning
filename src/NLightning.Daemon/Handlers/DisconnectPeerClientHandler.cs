using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Interfaces;
using Interfaces;

/// <summary>
/// Disconnects a connected peer (ClientCommand 24, NL-152).
/// </summary>
/// <remarks>
/// Refused with <see cref="ErrorCodes.InvalidOperation"/> while the peer's channels have HTLCs in flight, unless the
/// request forces it: a peer that stays away past an HTLC's deadline makes us fail its channel on chain. The check and
/// the disconnection are not atomic; an HTLC added in between is handled like any other disconnection
/// (channel_reestablish on the next connection).
/// A peer disconnected this way is not reconnected by the node (<see cref="IPeerManager.DisconnectPeer"/>) until the
/// next <c>connect</c> or a restart; the peer may still connect to us. Its temporary channels (opens not funded yet)
/// are forgotten (NL-392).
/// </remarks>
public sealed class DisconnectPeerClientHandler
    : IClientCommandHandler<DisconnectPeerClientRequest, DisconnectPeerClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<DisconnectPeerClientHandler> _logger;
    private readonly IPeerManager _peerManager;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.DisconnectPeer;

    public DisconnectPeerClientHandler(IChannelMemoryRepository channelMemoryRepository,
                                       ILogger<DisconnectPeerClientHandler> logger, IPeerManager peerManager)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _peerManager = peerManager;
    }

    /// <inheritdoc/>
    public Task<DisconnectPeerClientResponse> HandleAsync(DisconnectPeerClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_peerManager.GetPeer(request.NodeId) is null)
            throw new ClientException(ErrorCodes.InvalidOperation, $"Peer {request.NodeId} is not connected.");

        var summary = PeerChannelSummary.For(_channelMemoryRepository, request.NodeId);
        if (summary.HtlcsInFlight > 0 && !request.Force)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"Peer {request.NodeId} has {summary.HtlcsInFlight} HTLC(s) in flight on "
                                    + $"{summary.ChannelsWithHtlcs} channel(s); disconnecting it can force a channel on "
                                    + "chain if it does not come back before an HTLC times out. Use --force to "
                                    + "disconnect anyway.");

        _logger.LogInformation(
            "Disconnecting peer {Peer} on request ({Channels} channel(s), {Htlcs} HTLC(s) in flight{Forced}); it is not "
          + "reconnected until the next connect or restart", request.NodeId, summary.ChannelCount,
            summary.HtlcsInFlight, request.Force ? ", forced" : string.Empty);
        _peerManager.DisconnectPeer(request.NodeId);

        return Task.FromResult(new DisconnectPeerClientResponse(request.NodeId, summary.ChannelCount,
                                                                summary.HtlcsInFlight));
    }
}
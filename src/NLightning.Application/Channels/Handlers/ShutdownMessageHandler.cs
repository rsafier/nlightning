namespace NLightning.Application.Channels.Handlers;

using Close;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;

/// <summary>
/// Receives <c>shutdown</c> (BOLT 2 "Closing Initiation", plan N10-T3): see
/// <see cref="ChannelCloseCoordinator.ReceiveShutdownAsync"/>. <c>ChannelManager</c> moves the close on afterwards
/// (<see cref="ChannelCloseCoordinator.AdvanceAsync"/>).
/// </summary>
public class ShutdownMessageHandler : IChannelMessageHandler<ShutdownMessage>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ChannelCloseCoordinator _coordinator;

    public ShutdownMessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                  ChannelCloseCoordinator coordinator)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _coordinator = coordinator;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(ShutdownMessage message, ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channelId = message.Payload.ChannelId;

        // B2-SHUT-R01: a shutdown before funding_signed/funding_created names a channel we do not have yet
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel) || channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException($"[B2-SHUT-R01] shutdown for channel {channelId}, which is not active",
                                            channelId, "unknown channel");

        return await _coordinator.ReceiveShutdownAsync(channel, message, negotiatedFeatures);
    }
}
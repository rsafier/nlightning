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
/// Receives the legacy <c>closing_signed</c> (BOLT 2, plan N10-T3): see
/// <see cref="ChannelCloseCoordinator.ReceiveClosingSignedAsync"/>.
/// </summary>
public class ClosingSignedMessageHandler : IChannelMessageHandler<ClosingSignedMessage>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ChannelCloseCoordinator _coordinator;

    public ClosingSignedMessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                       ChannelCloseCoordinator coordinator)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _coordinator = coordinator;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(ClosingSignedMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channelId = message.Payload.ChannelId;

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel) || channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException($"closing_signed for channel {channelId}, which is not active",
                                            channelId, "unknown channel");

        return await _coordinator.ReceiveClosingSignedAsync(channel, message);
    }
}
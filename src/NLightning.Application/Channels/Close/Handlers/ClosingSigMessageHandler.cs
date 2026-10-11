namespace NLightning.Application.Channels.Close.Handlers;

using Application.Channels.Handlers.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Receives <c>closing_sig</c> (BOLT 2 <c>option_simple_close</c>, plan N11-T2): see
/// <see cref="ChannelCloseCoordinator.ReceiveClosingSigAsync"/>.
/// </summary>
public class ClosingSigMessageHandler : IChannelMessageHandler<ClosingSigMessage>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ChannelCloseCoordinator _coordinator;

    public ClosingSigMessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                    ChannelCloseCoordinator coordinator)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _coordinator = coordinator;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(ClosingSigMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channelId = message.Payload.ChannelId;

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel) || channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException($"closing_sig for channel {channelId}, which is not active",
                                            channelId, "unknown channel");

        return await _coordinator.ReceiveClosingSigAsync(channel, message, negotiatedFeatures);
    }
}
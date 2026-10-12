using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Handlers.Interfaces;

/// <summary>
/// Receives the peer's <c>accept_channel2</c> to our <c>open_channel2</c> (BOLT 2 "Channel Establishment v2", type 65):
/// <see cref="IDualFundedOpenService.HandleAcceptChannel2Async"/> derives the v2 channel id and starts the funding
/// negotiation as its initiator, under the temporary channel's lock <c>ChannelManager</c> holds.
/// </summary>
public sealed class AcceptChannel2MessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    : IChannelMessageHandler<AcceptChannel2Message>
{
    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(AcceptChannel2Message message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (serviceProvider.GetService<IDualFundedOpenService>() is not { } service)
            throw new ChannelErrorException("accept_channel2 without a dual-funding service",
                                            message.Payload.ChannelId, "unknown channel");

        return service.HandleAcceptChannel2Async(message, negotiatedFeatures, peerPubKey, unitOfWork);
    }
}
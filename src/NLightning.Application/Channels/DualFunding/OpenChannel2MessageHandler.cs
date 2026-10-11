using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Handlers.Interfaces;

/// <summary>
/// Receives a peer's <c>open_channel2</c> (BOLT 2 "Channel Establishment v2", type 64; splicing plan wave DF): we are
/// the accepter. The contribution comes from the accepter policy (<see cref="DualFundingOptions"/>), the rest is
/// <see cref="IDualFundedOpenService.AcceptAsync"/>, under the temporary channel's lock <c>ChannelManager</c> holds.
/// Without a dual-funding service the open is refused with an <c>error</c> for the temporary channel.
/// </summary>
public sealed class OpenChannel2MessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    : IChannelMessageHandler<OpenChannel2Message>
{
    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(OpenChannel2Message message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channelId = message.Payload.ChannelId;
        if (currentState != ChannelState.None)
            throw new ChannelErrorException("A channel with this id already exists", channelId);

        if (serviceProvider.GetService<IDualFundedOpenService>() is not { } service)
            throw new ChannelErrorException("open_channel2 without a dual-funding service", channelId,
                                            "dual-funded channels are not supported");

        var contribution = service is DualFundedOpenService ours
                               ? ours.GetAcceptContribution(message)
                               : LightningMoney.Zero;
        return service.AcceptAsync(message, negotiatedFeatures, peerPubKey, contribution, unitOfWork);
    }
}
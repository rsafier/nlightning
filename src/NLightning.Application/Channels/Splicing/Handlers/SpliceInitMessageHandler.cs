using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Splicing.Handlers;

using Channels.Handlers.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using InteractiveTx;
using InteractiveTx.Interfaces;

/// <summary>
/// Handles the peer's <c>splice_init</c> (BOLT 2 "Channel Splicing", type 80; splicing plan SP1-D-T2, SP-R-01). Thin:
/// under the channel's lock (<c>ChannelManager</c>) it hands the message to <see cref="ISpliceService"/>, which answers
/// <c>splice_ack</c> (D10: contribution 0) or <c>tx_abort</c>, or throws the <c>warning</c> of a broken rule. A node
/// without splicing rejects the splice with <c>tx_abort</c> (BOLT 2: "Otherwise (it rejects the splice): MUST respond
/// with <c>tx_abort</c>"), which also ends the quiescence.
/// </summary>
public sealed class SpliceInitMessageHandler : IChannelMessageHandler<SpliceInitMessage>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUnitOfWork _unitOfWork;

    public SpliceInitMessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    {
        _serviceProvider = serviceProvider;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(SpliceInitMessage message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_serviceProvider.GetService<ISpliceService>() is { } spliceService)
            return spliceService.HandleSpliceInitAsync(message, negotiatedFeatures, peerPubKey, _unitOfWork);

        var channelId = message.Payload.ChannelId;
        const string reason = "splicing is not supported";
        return _serviceProvider.GetService<IInteractiveTxDriver>() is { } driver
                   ? Task.FromResult(driver.AbortQuiescence(channelId, peerPubKey, reason))
                   : Task.FromResult<IReadOnlyList<IChannelMessage>>(
                       [InteractiveTxDriver.CreateTxAbort(channelId, reason)]);
    }
}
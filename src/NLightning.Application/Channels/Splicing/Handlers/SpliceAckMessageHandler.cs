using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Splicing.Handlers;

using Channels.Handlers.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Handles the peer's <c>splice_ack</c> (BOLT 2 "Channel Splicing", type 81; splicing plan SP1-D-T2, SP-R-02). Thin:
/// under the channel's lock it hands the message to <see cref="ISpliceService"/>, which starts the interactive-tx
/// negotiation as initiator (the shared input and output first) or answers <c>tx_abort</c>. A node without splicing
/// never sent <c>splice_init</c>: BOLT 2 "Otherwise (it has not sent <c>splice_init</c>): MUST send a <c>warning</c>
/// and close the connection".
/// </summary>
public sealed class SpliceAckMessageHandler : IChannelMessageHandler<SpliceAckMessage>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUnitOfWork _unitOfWork;

    public SpliceAckMessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    {
        _serviceProvider = serviceProvider;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(SpliceAckMessage message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_serviceProvider.GetService<ISpliceService>() is { } spliceService)
            return spliceService.HandleSpliceAckAsync(message, negotiatedFeatures, peerPubKey, _unitOfWork);

        throw new ChannelWarningException("[SP-R-02] splice_ack without our splice_init", message.Payload.ChannelId,
                                          "splice_ack without splice_init")
        {
            CloseConnection = true
        };
    }
}
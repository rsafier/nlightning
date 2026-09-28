using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using InteractiveTx;
using InteractiveTx.Interfaces;
using Interfaces;
using Splicing;

/// <summary>
/// Receives <c>tx_init_rbf</c> (BOLT 2 "Interactive Transaction Construction", type 72; splicing plan IT4-T2, SPR-T1).
/// On a channel past <c>channel_ready</c> it is an RBF of the pending splice and goes to <see cref="SpliceService"/>
/// (the splice rules first: warning and close, or <c>tx_abort</c>); otherwise (a dual-funded open) it goes to the
/// interactive-tx driver as before.
/// </summary>
public sealed class TxInitRbfMessageHandler : IChannelMessageHandler<TxInitRbfMessage>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IUnitOfWork _unitOfWork;

    public TxInitRbfMessageHandler(IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    {
        _serviceProvider = serviceProvider;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(TxInitRbfMessage message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channelId = message.Payload.ChannelId;
        if (_serviceProvider.GetService<SpliceService>() is { } splices && splices.HandlesRbf(channelId))
            return splices.HandleTxInitRbfAsync(message, negotiatedFeatures, peerPubKey, _unitOfWork);

        if (_serviceProvider.GetService<IInteractiveTxDriver>() is { } driver)
            return driver.ReceiveAsync(message, peerPubKey, _unitOfWork);

        return Task.FromResult<IReadOnlyList<IChannelMessage>>(
            [InteractiveTxDriver.CreateTxAbort(channelId, "interactive-tx not supported")]);
    }
}